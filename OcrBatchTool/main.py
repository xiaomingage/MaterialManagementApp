#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
工程单据 OCR 批量识别工具
=========================
目录约定（全部位于本脚本所在目录）:
    images/                待识别单据图片（jpg/jpeg/png/bmp/tif/tiff）
    standard_words.json    标准业务词库（模糊校正目标词）
    char_mistake.json      形近字错误映射表（错字 -> 正字）
    ocr_log.json           识别日志（每次运行追加一条，含原始/校正文本、置信度、相似度、审核状态）
    ocr_result_output.xlsx 识别结果台账 Excel（每次运行重新生成）

用法:
    python main.py                    # 批量识别 images/ 下全部图片
    python main.py --deskew           # 启用倾斜矫正（默认关闭）
    python main.py --learn "透水砖,级配碎石"           # 人工修正后的新词，自动追加进标准词库
    python main.py --learn-mistake "亳:毫,囯:国"       # 追加形近字映射

置信度分级:
    >= 0.60 直接采信
    0.40 ~ 0.60 进入模糊词库校正（形近字映射 + fuzzywuzzy 匹配，阈值 85）
    <  0.40 标记人工复核
模糊匹配低于阈值 85 的词保留原文并整行标记「待人工审核」。
"""

from __future__ import annotations

import argparse
import importlib
import json
import re
import sys
import time
from datetime import datetime
from pathlib import Path

BASE_DIR = Path(__file__).resolve().parent
IMAGES_DIR = BASE_DIR / "images"
WORDS_PATH = BASE_DIR / "standard_words.json"
MISTAKES_PATH = BASE_DIR / "char_mistake.json"
LOG_PATH = BASE_DIR / "ocr_log.json"
XLSX_PATH = BASE_DIR / "ocr_result_output.xlsx"

CONF_ACCEPT = 0.60   # >= 0.60 直接采信
CONF_FUZZY = 0.40    # 0.40 以下人工复核
SIM_THRESHOLD = 85   # 模糊匹配相似度阈值（0~100）
IMG_EXTS = {".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff"}

# 只对含中文的词做模糊匹配；纯数字/编号（如 35.5、C30、P.O42.5）不做校正
TOKEN_RE = re.compile(r"[\u4e00-\u9fffA-Za-z0-9.]+")
HAS_CJK_RE = re.compile(r"[\u4e00-\u9fff]")


def _ensure(module: str, pip_name: str):
    """按需导入第三方库，缺失时给出安装提示后退出。"""
    try:
        return importlib.import_module(module)
    except ImportError:
        print(f"[错误] 缺少依赖 {module}，请先执行:")
        print(f"    python -m pip install {pip_name} -i https://pypi.tuna.tsinghua.edu.cn/simple")
        sys.exit(1)


# ----------------------------------------------------------------------
# 图像预处理：灰度 -> CLAHE -> 中值去噪 -> (可选倾斜矫正) -> 自适应二值化 -> 形态学修复断笔画
# ----------------------------------------------------------------------
class ImagePreprocessor:
    def __init__(self, deskew: bool = False):
        self.deskew = deskew

    def process(self, bgr):
        cv2 = _ensure("cv2", "opencv-python")
        gray = cv2.cvtColor(bgr, cv2.COLOR_BGR2GRAY)

        # CLAHE 对比度增强：应对拍照光照不均
        clahe = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8))
        gray = clahe.apply(gray)

        # 中值滤波去椒盐噪点，核不能太大以免笔画细节丢失
        gray = cv2.medianBlur(gray, 3)

        if self.deskew:
            gray = self._deskew(gray)

        # 自适应二值化：局部阈值对手拍阴影更稳
        binary = cv2.adaptiveThreshold(
            gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, 31, 15)

        # 形态学闭运算修复断笔画（细笔画字体二值化后容易断裂）
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
        binary = cv2.morphologyEx(binary, cv2.MORPH_CLOSE, kernel)

        return cv2.cvtColor(binary, cv2.COLOR_GRAY2BGR)

    def _deskew(self, gray):
        """倾斜矫正：minAreaRect 估计文本主方向，仅矫正 ±0.5°~15° 内的小角度。"""
        cv2 = _ensure("cv2", "opencv-python")
        binary = cv2.adaptiveThreshold(
            gray, 255, cv2.ADAPTIVE_THRESH_GAUSSIAN_C, cv2.THRESH_BINARY, 25, 10)
        coords = cv2.findNonZero(cv2.bitwise_not(binary))
        if coords is None:
            return gray
        angle = cv2.minAreaRect(coords)[-1]
        if angle > 45:
            angle -= 90
        if abs(angle) < 0.5 or abs(angle) > 15:
            return gray
        h, w = gray.shape
        matrix = cv2.getRotationMatrix2D((w / 2, h / 2), angle, 1.0)
        return cv2.warpAffine(
            gray, matrix, (w, h), flags=cv2.INTER_CUBIC, borderValue=255)


# ----------------------------------------------------------------------
# 模糊识别校正库：形近字映射 + 标准词库 fuzzywuzzy 匹配，支持持续追加词条
# ----------------------------------------------------------------------
class FuzzyCorrector:
    def __init__(self, words_path: Path, mistakes_path: Path):
        self.words_path = words_path
        self.mistakes_path = mistakes_path
        self.words = self._load_json(words_path, [])
        self.mistakes = self._load_json(mistakes_path, {})
        self._lower_words = {str(w).lower() for w in self.words}

    @staticmethod
    def _load_json(path: Path, default):
        if not path.exists():
            return default
        try:
            return json.loads(path.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            print(f"[警告] {path.name} 解析失败（{exc}），使用默认值。")
            return default

    def _save_json(self, path: Path, data) -> None:
        path.write_text(
            json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")

    def apply_char_mistakes(self, text: str) -> str:
        """形近字错误映射：把 OCR 常见错字替换为正字。"""
        for wrong, right in self.mistakes.items():
            if wrong in text:
                text = text.replace(wrong, right)
        return text

    def add_standard_word(self, word: str) -> bool:
        """人工修正后的新词追加进标准词库（去重，大小写不敏感）。"""
        word = word.strip()
        if not word or word.lower() in self._lower_words:
            return False
        self.words.append(word)
        self._lower_words.add(word.lower())
        self._save_json(self.words_path, self.words)
        return True

    def add_mistake(self, wrong: str, right: str) -> bool:
        """追加形近字映射。"""
        wrong, right = wrong.strip(), right.strip()
        if not wrong or not right or wrong == right:
            return False
        self.mistakes[wrong] = right
        self._save_json(self.mistakes_path, self.mistakes)
        return True

    def _best_match(self, token: str):
        """在标准词库中找相似度最高的词。"""
        _ensure("fuzzywuzzy", "fuzzywuzzy python-Levenshtein")
        from fuzzywuzzy import fuzz  # ratio 在 fuzz 子模块，必须显式导入
        best_word, best_sim = None, 0
        for word in self.words:
            sim = fuzz.ratio(token, word)
            if sim > best_sim:
                best_word, best_sim = word, sim
        return best_word, best_sim

    def correct_line(self, raw: str) -> dict:
        """对 0.4~0.6 置信度行执行校正：先形近字映射（可信），再模糊匹配词库。"""
        corrected = raw
        best_sim = None
        low_count = 0
        details = []

        # dict.fromkeys 去重且保序，避免同一词重复匹配
        for token in dict.fromkeys(TOKEN_RE.findall(raw)):
            if not HAS_CJK_RE.search(token):
                continue

            # 1) 形近字映射命中：用户维护的可信修正，直接替换且不再模糊匹配
            mapped = self.apply_char_mistakes(token)
            if mapped != token:
                corrected = corrected.replace(token, mapped)
                details.append({
                    "token": token,
                    "suggestion": mapped,
                    "similarity": None,
                    "replaced": True,
                    "via": "形近字映射",
                })
                continue

            # 2) 已是标准词则跳过；过短的词不做模糊匹配（误判率高）
            if token.lower() in self._lower_words or len(token) < 2:
                continue

            # 3) fuzzywuzzy 模糊匹配标准词库
            word, sim = self._best_match(token)
            replaced = word is not None and sim >= SIM_THRESHOLD
            details.append({
                "token": token,
                "suggestion": word if replaced else None,
                "similarity": sim,
                "replaced": replaced,
            })
            if replaced:
                corrected = corrected.replace(token, word)
                best_sim = sim if best_sim is None else max(best_sim, sim)
            else:
                low_count += 1

        return {
            "corrected": corrected,
            "similarity": best_sim,
            "low_count": low_count,
            "tokens": details,
        }

    def classify(self, raw: str, conf: float) -> dict:
        """按置信度分级处理单行 OCR 结果。"""
        entry = {
            "raw": raw,
            "corrected": raw,
            "confidence": round(float(conf), 4),
            "similarity": None,
            "status": "",
            "tokens": [],
        }
        if conf >= CONF_ACCEPT:
            entry["status"] = "已采信"
        elif conf < CONF_FUZZY:
            entry["status"] = "人工复核"
        else:
            corr = self.correct_line(raw)
            entry["corrected"] = corr["corrected"]
            entry["similarity"] = corr["similarity"]
            entry["tokens"] = corr["tokens"]
            # 有词低于阈值 85 说明校正把握不足，整行转人工审核
            entry["status"] = "待人工审核" if corr["low_count"] else "模糊校正"
        return entry


# ----------------------------------------------------------------------
# PaddleOCR 封装：兼容 3.x predict / 2.x ocr 两种返回结构
# ----------------------------------------------------------------------
def create_ocr():
    _ensure("paddleocr", "paddlepaddle paddleocr")
    from paddleocr import PaddleOCR

    # PP-OCRv6 medium 在 paddle 3.3 CPU(oneDNN) 上有 Unimplemented 崩溃，
    # 因此优先用兼容性最好的 PP-OCRv5 mobile 并禁用 mkldnn
    attempts = (
        dict(
            text_detection_model_name="PP-OCRv5_mobile_det",
            text_recognition_model_name="PP-OCRv5_mobile_rec",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
            enable_mkldnn=False,
        ),
        dict(
            lang="ch",
            use_doc_orientation_classify=False,
            use_doc_unwarping=False,
            use_textline_orientation=False,
            enable_mkldnn=False,
        ),
        dict(lang="ch", use_angle_cls=True, show_log=False),
    )

    last_error = None
    for kwargs in attempts:
        try:
            return PaddleOCR(**kwargs)
        except TypeError as exc:  # 参数名随版本变化，逐个尝试
            last_error = exc
    raise last_error


def run_ocr(ocr, image) -> list:
    """返回 [(文本, 置信度), ...]，图片参数支持路径或 ndarray。"""
    try:
        result = ocr.predict(image)              # paddleocr >= 3.0
    except AttributeError:
        try:
            result = ocr.ocr(image, cls=True)    # paddleocr 2.x
        except TypeError:
            result = ocr.ocr(image)
    return extract_lines(result)


def extract_lines(result) -> list:
    lines = []
    if not result:
        return lines
    for block in result:
        if isinstance(block, dict) or hasattr(block, "get"):
            texts = block.get("rec_texts") or []
            scores = block.get("rec_scores") or []
            for text, score in zip(texts, scores):
                lines.append((str(text), float(score)))
        elif isinstance(block, list):
            for item in block:
                try:
                    lines.append((str(item[1][0]), float(item[1][1])))
                except (IndexError, TypeError, ValueError):
                    continue
    return lines


# ----------------------------------------------------------------------
# 批量识别主流程
# ----------------------------------------------------------------------
def process_image(ocr, preprocessor, corrector: FuzzyCorrector, path: Path) -> dict:
    cv2 = _ensure("cv2", "opencv-python")
    np = _ensure("numpy", "numpy")
    stamp = datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    t0 = time.time()

    # cv2.imread 不支持中文路径，必须用 np.fromfile + imdecode 读取
    data = np.fromfile(str(path), dtype=np.uint8)
    bgr = cv2.imdecode(data, cv2.IMREAD_COLOR) if data.size else None
    if bgr is None:
        print(f"    [警告] 图片读取失败，已跳过: {path.name}")
        return {"image": str(path.relative_to(BASE_DIR)), "time": stamp,
                "elapsed_ms": 0, "lines": []}

    proc = preprocessor.process(bgr)
    lines = run_ocr(ocr, proc)
    if not lines:
        # 预处理图像识别为空时回退原图（个别单据强二值化反而丢字）
        lines = run_ocr(ocr, bgr)

    entries = [corrector.classify(text, conf) for text, conf in lines]
    elapsed = int((time.time() - t0) * 1000)
    print(f"    文本行 {len(entries)}，耗时 {elapsed} ms")

    return {
        "image": str(path.relative_to(BASE_DIR)),
        "time": stamp,
        "elapsed_ms": elapsed,
        "lines": entries,
    }


def save_log(run_entry: dict) -> None:
    """ocr_log.json 按运行次数追加，历史可追溯。"""
    history = []
    if LOG_PATH.exists():
        try:
            history = json.loads(LOG_PATH.read_text(encoding="utf-8"))
            if isinstance(history, dict):
                history = [history]
        except (json.JSONDecodeError, OSError) as exc:
            print(f"[警告] 旧日志解析失败（{exc}），将重建 ocr_log.json。")
            history = []
    history.append(run_entry)
    LOG_PATH.write_text(
        json.dumps(history, ensure_ascii=False, indent=2), encoding="utf-8")


def export_excel(results: list) -> None:
    """识别校正完成后导出台账 Excel，待审核行淡红底标出。"""
    openpyxl = _ensure("openpyxl", "openpyxl")
    from openpyxl.styles import Alignment, Font, PatternFill

    wb = openpyxl.Workbook()
    ws = wb.active
    ws.title = "识别结果"
    headers = ["图片文件", "识别时间", "原始OCR文本", "校正后文本",
               "OCR置信度", "模糊相似度", "审核状态"]
    ws.append(headers)
    for cell in ws[1]:
        cell.font = Font(bold=True, color="FFFFFF")
        cell.fill = PatternFill("solid", start_color="4472C4")
        cell.alignment = Alignment(horizontal="center", vertical="center")

    review_fill = PatternFill("solid", start_color="FCE4EC")
    for result in results:
        for line in result["lines"]:
            row = [
                result["image"],
                result["time"],
                line["raw"],
                line["corrected"],
                line["confidence"],
                line["similarity"] if line["similarity"] is not None else "",
                line["status"],
            ]
            ws.append(row)
            if line["status"] in ("待人工审核", "人工复核"):
                for cell in ws[ws.max_row]:
                    cell.fill = review_fill

    for idx, width in enumerate((26, 20, 46, 46, 12, 12, 12), start=1):
        ws.column_dimensions[openpyxl.utils.get_column_letter(idx)].width = width
    ws.freeze_panes = "A2"
    wb.save(XLSX_PATH)


# ----------------------------------------------------------------------
# ROI 自定义区域识别：按模板把图上的命名矩形区域单独提取识别
# ----------------------------------------------------------------------
ROI_TEMPLATES_PATH = BASE_DIR / "roi_templates.json"
ROI_XLSX_PATH = BASE_DIR / "ocr_roi_output.xlsx"


def load_roi_templates() -> dict:
    """读取 ROI 模板: {模板名: [{name,x,y,w,h}, ...]}，坐标为 0~1 相对比例。"""
    if ROI_TEMPLATES_PATH.exists():
        try:
            return json.loads(ROI_TEMPLATES_PATH.read_text(encoding="utf-8"))
        except (json.JSONDecodeError, OSError) as exc:
            print(f"[警告] ROI 模板读取失败: {exc}")
    return {}


def save_roi_templates(data: dict) -> None:
    ROI_TEMPLATES_PATH.write_text(
        json.dumps(data, ensure_ascii=False, indent=2), encoding="utf-8")


def crop_roi(bgr, roi: dict):
    """按相对比例坐标裁剪区域，越界自动收敛。"""
    h, w = bgr.shape[:2]
    x1 = max(0, min(w - 1, int(roi["x"] * w)))
    y1 = max(0, min(h - 1, int(roi["y"] * h)))
    x2 = max(x1 + 1, min(w, int((roi["x"] + roi["w"]) * w)))
    y2 = max(y1 + 1, min(h, int((roi["y"] + roi["h"]) * h)))
    return bgr[y1:y2, x1:x2]


def run_roi_ocr(ocr, bgr, rois: list, preprocessor=None,
                corrector: "FuzzyCorrector" = None) -> dict:
    """对每个命名区域单独裁剪、预处理、识别并校正。

    返回 {字段名: {text, confidence, review}}，review 表示该字段需要人工复核。
    """
    fields = {}
    for roi in rois:
        crop = crop_roi(bgr, roi)
        if crop.size == 0:
            fields[roi["name"]] = {"text": "", "confidence": 0.0, "review": True}
            continue
        proc = preprocessor.process(crop) if preprocessor else crop
        lines = run_ocr(ocr, proc)
        if not lines and preprocessor:  # 预处理图识别为空时回退原图
            lines = run_ocr(ocr, crop)
        if corrector:
            corrected = [corrector.classify(t, c) for t, c in lines]
            text = " ".join(line["corrected"] for line in corrected)
            conf = min((line["confidence"] for line in corrected), default=0.0)
            review = any(line["status"] in ("待人工审核", "人工复核")
                         for line in corrected)
        else:
            text = " ".join(t for t, _ in lines)
            conf = min((c for _, c in lines), default=0.0)
            review = conf < LOW_CONF
        fields[roi["name"]] = {"text": text, "confidence": conf, "review": review}
    return fields


def export_roi_excel(results: list, xlsx_path=None) -> Path:
    """导出 ROI 结构化提取结果：一行一张图，一列一个字段，需复核淡红底。"""
    openpyxl = _ensure("openpyxl", "openpyxl")
    from openpyxl.styles import Alignment, Font, PatternFill

    xlsx_path = Path(xlsx_path) if xlsx_path else ROI_XLSX_PATH
    field_names = []
    for result in results:
        for name in result.get("fields", {}):
            if name not in field_names:
                field_names.append(name)

    wb = openpyxl.Workbook()
    ws = wb.active 
    ws.title = "ROI识别结果"
    ws.append(["图片文件", "识别时间"] + field_names)
    for cell in ws[1]:
        cell.font = Font(bold=True, color="FFFFFF")
        cell.fill = PatternFill("solid", start_color="4472C4")
        cell.alignment = Alignment(horizontal="center", vertical="center")

    review_fill = PatternFill("solid", start_color="FCE4EC")
    for row_idx, result in enumerate(results, start=2):
        ws.cell(row_idx, 1, result["image"])
        ws.cell(row_idx, 2, result.get("time", ""))
        fields = result.get("fields", {})
        for col_idx, name in enumerate(field_names, start=3):
            field = fields.get(name, {"text": "", "review": False})
            cell = ws.cell(row_idx, col_idx, field["text"])
            if field.get("review"):
                cell.fill = review_fill

    widths = {1: 30, 2: 20}
    for idx, name in enumerate(field_names, start=3):
        widths[idx] = max(14, min(30, len(name) * 2 + 8))
    for idx, width in widths.items():
        ws.column_dimensions[openpyxl.utils.get_column_letter(idx)].width = width
    ws.freeze_panes = "A2"
    wb.save(xlsx_path)
    return xlsx_path


def main() -> None:
    parser = argparse.ArgumentParser(description="工程单据 OCR 批量识别工具")
    parser.add_argument("--deskew", action="store_true", help="启用倾斜矫正")
    parser.add_argument("--learn", default="",
                        help="人工修正后的新词，逗号分隔，自动追加进标准词库")
    parser.add_argument("--learn-mistake", dest="learn_mistake", default="",
                        help="追加形近字映射，格式: 错字:正字,错字2:正字2")
    args = parser.parse_args()

    corrector = FuzzyCorrector(WORDS_PATH, MISTAKES_PATH)

    # 词库迭代：先处理学习参数，便于不跑识别也能维护词库
    if args.learn:
        for word in re.split(r"[,，]", args.learn):
            if corrector.add_standard_word(word):
                print(f"[词库] 新增标准词: {word}")
            else:
                print(f"[词库] 跳过（已存在或为空）: {word}")
    if args.learn_mistake:
        for pair in re.split(r"[,，]", args.learn_mistake):
            if ":" not in pair:
                continue
            wrong, right = pair.split(":", 1)
            if corrector.add_mistake(wrong, right):
                print(f"[词库] 新增形近字映射: {wrong} -> {right}")

    IMAGES_DIR.mkdir(exist_ok=True)
    images = sorted(p for p in IMAGES_DIR.iterdir()
                    if p.suffix.lower() in IMG_EXTS)

    if not images:
        print(f"images 目录为空，请把单据图片放入: {IMAGES_DIR}")
        print("支持格式: jpg / jpeg / png / bmp / tif / tiff")
        return

    print(f"共 {len(images)} 张图片，开始识别（倾斜矫正: {'开' if args.deskew else '关'}）...")
    _ensure("numpy", "numpy")
    ocr = create_ocr()
    preprocessor = ImagePreprocessor(deskew=args.deskew)

    results = []
    for idx, path in enumerate(images, 1):
        print(f"[{idx}/{len(images)}] {path.name}")
        try:
            results.append(process_image(ocr, preprocessor, corrector, path))
        except Exception as exc:  # 单张失败不中断整批
            print(f"    [错误] 识别失败: {exc}")
            results.append({
                "image": str(path.relative_to(BASE_DIR)),
                "time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                "elapsed_ms": 0,
                "lines": [],
                "error": str(exc),
            })

    save_log({
        "run_time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
        "deskew": bool(args.deskew),
        "image_count": len(results),
        "results": results,
    })
    export_excel(results)

    # 控制台汇总
    counter = {"已采信": 0, "模糊校正": 0, "待人工审核": 0, "人工复核": 0}
    total = 0
    for result in results:
        for line in result["lines"]:
            counter[line["status"]] = counter.get(line["status"], 0) + 1
            total += 1
    print("-" * 48)
    print(f"识别完成: {len(results)} 张图片, {total} 行文本")
    for status, count in counter.items():
        print(f"  {status}: {count} 行")
    print(f"日志: {LOG_PATH}")
    print(f"Excel: {XLSX_PATH}")


if __name__ == "__main__":
    main()
