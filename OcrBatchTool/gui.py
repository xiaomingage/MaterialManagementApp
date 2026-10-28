#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
工程单据 OCR 批量识别工具 - GUI
================================
用 tkinter 实现（Python 自带，无额外依赖），复用 main.py 中已验证的
预处理 / PaddleOCR / 模糊校正 / Excel 导出逻辑。

功能:
    1. 选择图片文件夹（默认 images/），批量识别，实时进度与结果表格
    2. 人工编辑校正结果（双击行或「编辑选中行」）
    3. 一键把校正后的词追加进标准词库 / 追加形近字映射
    4. 导出 Excel（含人工修正状态，待审核行淡红底）

运行:
    python gui.py     （或 pythonw gui.py 隐藏控制台窗口）
"""

from __future__ import annotations

import queue
import sys
import threading
from datetime import datetime
from pathlib import Path
from tkinter import filedialog, messagebox, simpledialog, ttk
import tkinter as tk

BASE_DIR = Path(__file__).resolve().parent
sys.path.insert(0, str(BASE_DIR))

import main  # noqa: E402  复用预处理/OCR/校正/导出逻辑

STATUS_EDITED = "人工修正"
FONT = ("Microsoft YaHei UI", 9)


# ----------------------------------------------------------------------
# 批量识别（纯函数，可脱离 UI 测试）：progress_cb(第几张, 总数, 文件名)
# ----------------------------------------------------------------------
def run_batch(folder: Path, deskew: bool, progress_cb=None):
    """识别 folder 下全部图片，返回与 main.py 相同结构的结果列表。"""
    import cv2
    import numpy as np

    images = sorted(p for p in folder.iterdir()
                    if p.suffix.lower() in main.IMG_EXTS)
    if not images:
        return []

    ocr = main.create_ocr()
    preprocessor = main.ImagePreprocessor(deskew=deskew)
    corrector = _get_corrector()
    results = []
    total = len(images)
    for idx, path in enumerate(images, 1):
        if progress_cb:
            progress_cb(idx, total, path.name)
        try:
            # cv2.imread 不支持中文路径，必须用 np.fromfile + imdecode
            bgr = cv2.imdecode(
                np.fromfile(str(path), dtype=np.uint8), cv2.IMREAD_COLOR)
            if bgr is None:
                raise ValueError("图片解码失败")
            proc = preprocessor.process(bgr)
            lines = main.run_ocr(ocr, proc)
            if not lines:  # 预处理图识别为空时回退原图
                lines = main.run_ocr(ocr, bgr)
            results.append({
                "image": str(path.relative_to(BASE_DIR)),
                "time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                "elapsed_ms": 0,
                "lines": [corrector.classify(t, c) for t, c in lines],
            })
        except Exception as exc:  # 单张失败不中断整批
            results.append({
                "image": str(path.relative_to(BASE_DIR)),
                "time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                "elapsed_ms": 0,
                "lines": [],
                "error": str(exc),
            })
    return results


_corrector = None


def _get_corrector() -> main.FuzzyCorrector:
    """进程内共享词库校正器：GUI 的人工修正立即对后续识别生效。"""
    global _corrector
    if _corrector is None:
        _corrector = main.FuzzyCorrector(main.WORDS_PATH, main.MISTAKES_PATH)
    return _corrector


# ----------------------------------------------------------------------
# 界面
# ----------------------------------------------------------------------
class OcrGui:
    def __init__(self, root):
        self.root = root
        self.results = []          # 当前识别结果（可被人工编辑修改）
        self._row_map = {}         # tree iid -> (结果下标, 行下标)
        self._queue = queue.Queue()
        self._running = False

        root.title("工程单据 OCR 批量识别工具")
        root.geometry("1160x700")
        try:  # Windows 高分屏清晰度
            import ctypes
            ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except Exception:
            pass

        style = ttk.Style(root)
        style.configure("Treeview", font=FONT, rowheight=24)
        style.configure("Treeview.Heading", font=("Microsoft YaHei UI", 9, "bold"))
        style.configure("TButton", font=FONT)
        style.configure("TLabel", font=FONT)
        style.configure("TCheckbutton", font=FONT)

        self._build_toolbar()
        self._build_progress()
        self._build_table()
        self._build_bottom()

        root.after(80, self._poll_queue)

    # ---------------- 界面搭建 ----------------
    def _build_toolbar(self):
        bar = ttk.Frame(self.root, padding=(10, 10, 10, 4))
        bar.pack(fill="x")

        ttk.Label(bar, text="图片文件夹:").pack(side="left")
        self.folder_var = tk.StringVar(value=str(BASE_DIR / "images"))
        ttk.Entry(bar, textvariable=self.folder_var, width=52).pack(
            side="left", padx=(6, 4))
        ttk.Button(bar, text="浏览...", command=self._browse_folder).pack(
            side="left", padx=(0, 12))

        self.deskew_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(bar, text="倾斜矫正", variable=self.deskew_var).pack(
            side="left", padx=(0, 12))

        self.start_btn = ttk.Button(
            bar, text="开始识别", command=self._start_recognition)
        self.start_btn.pack(side="left")

        self.export_btn = ttk.Button(
            bar, text="导出 Excel", command=self._export_excel, state="disabled")
        self.export_btn.pack(side="left", padx=(8, 0))

    def _build_progress(self):
        bar = ttk.Frame(self.root, padding=(10, 2))
        bar.pack(fill="x")
        self.progress = ttk.Progressbar(bar, mode="determinate")
        self.progress.pack(fill="x")
        self.status_var = tk.StringVar(
            value="把单据图片放入 images 文件夹（或点击浏览选择），点击「开始识别」")
        ttk.Label(bar, textvariable=self.status_var).pack(anchor="w", pady=(4, 0))

    def _build_table(self):
        wrap = ttk.Frame(self.root, padding=(10, 6))
        wrap.pack(fill="both", expand=True)
        columns = ("image", "raw", "corrected", "conf", "sim", "status")
        self.tree = ttk.Treeview(wrap, columns=columns, show="headings")
        headers = [("image", "图片文件", 150, "w"), ("raw", "原始OCR文本", 300, "w"),
                   ("corrected", "校正后文本", 300, "w"), ("conf", "置信度", 70, "e"),
                   ("sim", "相似度", 70, "e"), ("status", "审核状态", 90, "center")]
        for col, text, width, anchor in headers:
            self.tree.heading(col, text=text)
            self.tree.column(col, width=width, anchor=anchor)
        vsb = ttk.Scrollbar(wrap, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=vsb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")
        self.tree.tag_configure("review", background="#FCE4EC")
        self.tree.tag_configure("edited", background="#E8F5E9")
        self.tree.bind("<Double-1>", lambda e: self._edit_selected())

    def _build_bottom(self):
        bar = ttk.Frame(self.root, padding=(10, 4, 10, 10))
        bar.pack(fill="x")
        self.edit_btn = ttk.Button(
            bar, text="编辑选中行", command=self._edit_selected, state="disabled")
        self.edit_btn.pack(side="left")
        self.add_word_btn = ttk.Button(
            bar, text="选中行加入词库...", command=self._append_words,
            state="disabled")
        self.add_word_btn.pack(side="left", padx=(8, 0))
        self.add_mistake_btn = ttk.Button(
            bar, text="追加形近字映射...", command=self._append_mistake)
        self.add_mistake_btn.pack(side="left", padx=(8, 0))
        ttk.Button(
            bar, text="ROI 区域识别...",
            command=lambda: RoiEditor(self.root, lambda: self.folder_var.get())
        ).pack(side="right")

    # ---------------- 交互动作 ----------------
    def _browse_folder(self):
        chosen = filedialog.askdirectory(title="选择单据图片文件夹")
        if chosen:
            self.folder_var.set(chosen)

    def _start_recognition(self):
        if self._running:
            return
        folder = Path(self.folder_var.get().strip())
        if not folder.is_dir():
            messagebox.showwarning("提示", f"文件夹不存在：\n{folder}")
            return

        self._running = True
        self._row_map.clear()
        for iid in self.tree.get_children():
            self.tree.delete(iid)
        self.progress.configure(value=0, maximum=100)
        self._set_buttons(False)
        self.status_var.set("正在加载 OCR 模型...")

        deskew = self.deskew_var.get()

        def progress_cb(idx, total, name):
            self._queue.put(("progress", idx, total, name))

        def worker():
            try:
                results = run_batch(folder, deskew, progress_cb)
                self._queue.put(("done", results, None))
            except Exception as exc:  # 模型加载失败等
                self._queue.put(("done", [], str(exc)))

        threading.Thread(target=worker, daemon=True).start()

    def _poll_queue(self):
        try:
            while True:
                msg = self._queue.get_nowait()
                kind = msg[0]
                if kind == "progress":
                    _, idx, total, name = msg
                    self.progress.configure(maximum=total, value=idx - 1)
                    self.status_var.set(f"识别中 {idx}/{total}: {name}")
                elif kind == "done":
                    _, results, error = msg
                    self.progress.configure(
                        value=self.progress.cget("maximum") or 100)
                    if error:
                        messagebox.showerror("识别失败", error)
                        self.status_var.set(f"识别失败: {error}")
                    else:
                        self.results = results
                        self._fill_table()
                        self.status_var.set(self._summary_text(results))
                    self._running = False
                    self._set_buttons(True)
        except queue.Empty:
            pass
        self.root.after(80, self._poll_queue)

    def _fill_table(self):
        for result in self.results:
            for line in result.get("lines", []):
                iid = self.tree.insert("", "end", values=(
                    Path(result["image"]).name,
                    line["raw"], line["corrected"],
                    f"{line['confidence']:.2f}",
                    "" if line["similarity"] is None else line["similarity"],
                    line["status"]))
                self._row_map[iid] = (len(self.results) - 1,
                                      len(result["lines"]) - 1)

    def _summary_text(self, results) -> str:
        counter = {}
        total = 0
        for result in results:
            for line in result.get("lines", []):
                counter[line["status"]] = counter.get(line["status"], 0) + 1
                total += 1
        parts = " | ".join(f"{s} {n} 行" for s, n in counter.items())
        return f"识别完成: {len(results)} 张图片, {total} 行文本　{parts}"

    def _set_buttons(self, enabled: bool):
        state = "normal" if enabled else "disabled"
        self.start_btn.configure(state=state)
        self.export_btn.configure(
            state=state if self.results else "disabled")
        self.edit_btn.configure(state=state if self.results else "disabled")
        self.add_word_btn.configure(state=state if self.results else "disabled")
        self.add_mistake_btn.configure(state="normal")

    # ---------------- 人工编辑 ----------------
    def _selected_line(self):
        iid = self.tree.selection()
        if not iid or iid[0] not in self._row_map:
            messagebox.showinfo("提示", "请先在表格中选中一行。")
            return None
        ri, li = self._row_map[iid[0]]
        return iid[0], ri, li, self.results[ri]["lines"][li]

    def _edit_selected(self):
        sel = self._selected_line()
        if sel is None:
            return
        iid, ri, li, line = sel

        dlg = tk.Toplevel(self.root)
        dlg.title("编辑校正结果")
        dlg.geometry("640x230")
        dlg.transient(self.root)
        dlg.grab_set()

        ttk.Label(dlg, text="图片:", font=FONT).grid(
            row=0, column=0, sticky="e", padx=10, pady=(14, 4))
        ttk.Label(dlg, text=Path(self.results[ri]["image"]).name).grid(
            row=0, column=1, sticky="w")
        ttk.Label(dlg, text="原始OCR:", font=FONT).grid(
            row=1, column=0, sticky="e", padx=10, pady=4)
        ttk.Label(dlg, text=line["raw"], wraplength=460).grid(
            row=1, column=1, sticky="w")
        ttk.Label(dlg, text="校正后:", font=FONT).grid(
            row=2, column=0, sticky="e", padx=10, pady=4)
        var = tk.StringVar(value=line["corrected"])
        entry = ttk.Entry(dlg, textvariable=var, width=58)
        entry.grid(row=2, column=1, sticky="w", pady=4)
        entry.focus_set()
        entry.select_range(0, "end")

        def save():
            new_text = var.get().strip()
            if not new_text:
                messagebox.showwarning("提示", "校正后文本不能为空。", parent=dlg)
                return
            line["corrected"] = new_text
            line["status"] = STATUS_EDITED
            self.tree.set(iid, "corrected", new_text)
            self.tree.set(iid, "status", STATUS_EDITED)
            self.tree.item(iid, tags=("edited",))
            dlg.destroy()

        btns = ttk.Frame(dlg)
        btns.grid(row=3, column=1, sticky="w", pady=(10, 0))
        ttk.Button(btns, text="保存", command=save).pack(side="left")
        ttk.Button(btns, text="取消", command=dlg.destroy).pack(
            side="left", padx=(8, 0))
        dlg.bind("<Return>", lambda e: save())
        dlg.bind("<Escape>", lambda e: dlg.destroy())

    def _append_words(self):
        sel = self._selected_line()
        if sel is None:
            return
        _, _, _, line = sel
        import re as _re
        tokens = [t for t in dict.fromkeys(
            _re.findall(r"[\u4e00-\u9fff]{2,}", line["corrected"]))
            if t not in _get_corrector().words]

        dlg = tk.Toplevel(self.root)
        dlg.title("把词追加进标准词库")
        dlg.geometry("380x340")
        dlg.transient(self.root)
        dlg.grab_set()
        ttk.Label(dlg, text="勾选要追加的词（来自校正后文本）:",
                  font=FONT).pack(anchor="w", padx=12, pady=(12, 4))
        listbox = tk.Listbox(dlg, selectmode="extended", font=FONT)
        for token in tokens or ["（没有可追加的新词）"]:
            listbox.insert("end", token)
        listbox.pack(fill="both", expand=True, padx=12, pady=4)
        if not tokens:
            listbox.configure(state="disabled")

        def confirm():
            chosen = [listbox.get(i) for i in listbox.curselection()]
            if not chosen:
                messagebox.showinfo("提示", "请先勾选要追加的词。", parent=dlg)
                return
            added = [w for w in chosen if _get_corrector().add_standard_word(w)]
            messagebox.showinfo(
                "完成",
                f"成功追加 {len(added)} 个新词:\n" + "\n".join(added),
                parent=dlg)
            dlg.destroy()

        ttk.Button(dlg, text="追加所选词", command=confirm).pack(pady=(4, 12))

    def _append_mistake(self):
        wrong = simpledialog.askstring(
            "追加形近字映射", "错字（OCR 常识别错的字/词）:", parent=self.root)
        if not wrong:
            return
        right = simpledialog.askstring(
            "追加形近字映射", f"正字（「{wrong}」应为）:", parent=self.root)
        if not right:
            return
        if _get_corrector().add_mistake(wrong.strip(), right.strip()):
            messagebox.showinfo("完成", f"已追加映射: {wrong} -> {right}")
        else:
            messagebox.showinfo("提示", "映射已存在或输入为空，未修改。")

    # ---------------- 导出 ----------------
    def _export_excel(self):
        if not self.results:
            messagebox.showinfo("提示", "还没有识别结果，请先「开始识别」。")
            return
        try:
            main.export_excel(self.results)
            main.save_log({
                "run_time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                "deskew": self.deskew_var.get(),
                "image_count": len(self.results),
                "results": self.results,
            })
            messagebox.showinfo(
                "完成", f"已导出:\n{main.XLSX_PATH}\n\n日志: {main.LOG_PATH}")
        except Exception as exc:
            messagebox.showerror("导出失败", str(exc))


# ----------------------------------------------------------------------
# ROI 自定义区域识别编辑器
# ----------------------------------------------------------------------
class RoiEditor:
    """在示例图上拖拽画框定义命名区域，保存为模板后可对整个文件夹批量提取字段。"""

    CANVAS_W, CANVAS_H = 820, 560

    def __init__(self, master, get_folder):
        self.master = master
        self.get_folder = get_folder
        self.templates = main.load_roi_templates()
        self.rois = []            # [{name,x,y,w,h}]，坐标 0~1 相对比例
        self._rgb = None          # PIL 原图
        self._photo = None        # 防止 PhotoImage 被 GC
        self._scale = 1.0
        self._disp = (0, 0)       # 显示尺寸 (宽, 高)
        self._drag_start = None
        self._temp_rect = None

        from PIL import Image, ImageTk  # noqa: F401  ImageTk 供 redraw 使用
        self._ImageTk = ImageTk

        self.win = tk.Toplevel(master)
        self.win.title("ROI 自定义区域识别")
        self.win.geometry("1240x780")
        self.win.transient(master)

        self._build_top()
        self._build_canvas_area()
        self._build_bottom()

        self.queue = queue.Queue()
        self._busy = False
        self.win.after(80, self._poll_queue)

        folder = Path(get_folder())
        if folder.is_dir():
            for p in sorted(folder.iterdir()):
                if p.suffix.lower() in main.IMG_EXTS:
                    self._load_image(p)
                    break
        self._refresh_combo()
        self._redraw()

    # ---------------- 界面搭建 ----------------
    def _build_top(self):
        bar = ttk.Frame(self.win, padding=(10, 10, 10, 4))
        bar.pack(fill="x")

        ttk.Button(bar, text="选择示例图片...", command=self._pick_image).pack(
            side="left")

        ttk.Label(bar, text="　模板:").pack(side="left")
        self.combo = ttk.Combobox(bar, width=18, state="readonly")
        self.combo.pack(side="left", padx=(4, 4))
        ttk.Button(bar, text="加载", command=self._load_template).pack(side="left")
        ttk.Button(bar, text="删除模板", command=self._delete_template).pack(
            side="left", padx=(4, 12))

        ttk.Label(bar, text="当前模板名:").pack(side="left")
        self.name_var = tk.StringVar()
        ttk.Entry(bar, textvariable=self.name_var, width=16).pack(
            side="left", padx=(4, 4))
        ttk.Button(bar, text="保存模板", command=self._save_template).pack(
            side="left", padx=(0, 12))

        self.trial_btn = ttk.Button(
            bar, text="试识别示例图", command=self._trial_run)
        self.trial_btn.pack(side="left")

    def _build_canvas_area(self):
        wrap = ttk.Frame(self.win, padding=(10, 6))
        wrap.pack(fill="both", expand=True)

        self.canvas = tk.Canvas(wrap, width=self.CANVAS_W, height=self.CANVAS_H,
                                bg="#DDDDDD", cursor="crosshair")
        self.canvas.pack(side="left", anchor="nw")
        self.canvas.bind("<ButtonPress-1>", self._on_press)
        self.canvas.bind("<B1-Motion>", self._on_drag)
        self.canvas.bind("<ButtonRelease-1>", self._on_release)

        side = ttk.Frame(wrap)
        side.pack(side="left", fill="both", expand=True, padx=(10, 0))
        ttk.Label(side, text="已定义区域:", font=FONT).pack(anchor="w")
        self.listbox = tk.Listbox(side, font=FONT)
        self.listbox.pack(fill="both", expand=True, pady=(4, 6))
        ttk.Button(side, text="删除选中区域",
                   command=self._delete_roi).pack(anchor="w")
        self.roi_status = tk.StringVar(
            value="在图上按住左键拖拽画框，松开后输入字段名（如: 物料编号/日期/强度标号）")
        ttk.Label(side, textvariable=self.roi_status, wraplength=300,
                  justify="left").pack(anchor="w", pady=(8, 0))

    def _build_bottom(self):
        bar = ttk.Frame(self.win, padding=(10, 4, 10, 10))
        bar.pack(fill="x")
        self.batch_btn = ttk.Button(
            bar, text="开始批量识别（整个文件夹）", command=self._start_batch)
        self.batch_btn.pack(side="left")
        self.roi_deskew_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            bar, text="对整图倾斜矫正（版面会变化，慎用）",
            variable=self.roi_deskew_var).pack(side="left", padx=(12, 0))
        self.status_var = tk.StringVar(value="就绪")
        ttk.Label(bar, textvariable=self.status_var).pack(side="right")

    # ---------------- 图片与画框 ----------------
    def _pick_image(self):
        chosen = filedialog.askopenfilename(
            title="选择示例图片",
            filetypes=[("图片", "*.jpg *.jpeg *.png *.bmp *.tif *.tiff")])
        if chosen:
            self._load_image(Path(chosen))

    def _load_image(self, path: Path):
        import cv2
        import numpy as np
        from PIL import Image

        try:
            data = np.fromfile(str(path), dtype=np.uint8)
            bgr = cv2.imdecode(data, cv2.IMREAD_COLOR)
            if bgr is None:
                raise ValueError("图片解码失败")
        except Exception as exc:
            messagebox.showerror("加载失败", str(exc), parent=self.win)
            return
        self._sample_path = path
        rgb = cv2.cvtColor(bgr, cv2.COLOR_BGR2RGB)
        self._rgb = Image.fromarray(rgb)
        w, h = self._rgb.size
        self._scale = min(1.0, self.CANVAS_W / w, self.CANVAS_H / h)
        self._disp = (int(w * self._scale), int(h * self._scale))
        self._photo = self._ImageTk.PhotoImage(
            self._rgb.resize(self._disp, Image.LANCZOS))
        self.canvas.configure(width=self._disp[0], height=self._disp[1])
        self._redraw()
        self.roi_status.set(f"示例图: {path.name}（{w}×{h}）")

    def _redraw(self):
        self.canvas.delete("all")
        if self._photo is not None:
            self.canvas.create_image(0, 0, image=self._photo, anchor="nw")
        for roi in self.rois:
            self._draw_roi(roi, outline="#E53935", label_color="#E53935")
        if self._temp_rect:
            self.canvas.coords(self._temp_rect[0], *self._temp_rect[1])

    def _draw_roi(self, roi, outline, label_color):
        dw, dh = self._disp
        x1, y1 = roi["x"] * dw, roi["y"] * dh
        x2, y2 = x1 + roi["w"] * dw, y1 + roi["h"] * dh
        self.canvas.create_rectangle(x1, y1, x2, y2, outline=outline, width=2)
        self.canvas.create_text(
            x1 + 4, max(10, y1 - 9), text=roi["name"], anchor="w",
            fill=label_color, font=("Microsoft YaHei UI", 10, "bold"))

    def _on_press(self, event):
        self._drag_start = (event.x, event.y)
        self._temp_rect = (self.canvas.create_rectangle(
            event.x, event.y, event.x, event.y, outline="#1E88E5",
            width=2, dash=(4, 2)), (event.x, event.y, event.x, event.y))

    def _on_drag(self, event):
        if self._drag_start:
            x0, y0 = self._drag_start
            coords = (x0, y0, event.x, event.y)
            self._temp_rect = (self._temp_rect[0], coords)
            self.canvas.coords(self._temp_rect[0], *coords)

    def _on_release(self, event):
        if not self._drag_start:
            return
        rect_id, _ = self._temp_rect
        self.canvas.delete(rect_id)
        x0, y0 = self._drag_start
        self._drag_start = None
        self._temp_rect = None
        if self._photo is None:
            return
        dw, dh = self._disp
        x1, x2 = sorted((min(x0, event.x), min(max(x0, event.x), dw)))
        y1, y2 = sorted((min(y0, event.y), min(max(y0, event.y), dh)))
        if (x2 - x1) < 8 or (y2 - y1) < 8:  # 过小的框视为误操作
            return
        name = simpledialog.askstring(
            "区域名称", "字段名（如: 物料编号 / 日期 / 强度标号）:",
            parent=self.win)
        if not name:
            return
        name = name.strip()
        # 同名区域直接替换，避免提取结果相互覆盖
        self.rois = [r for r in self.rois if r["name"] != name]
        self.rois.append({
            "name": name,
            "x": round(x1 / dw, 4), "y": round(y1 / dh, 4),
            "w": round((x2 - x1) / dw, 4), "h": round((y2 - y1) / dh, 4),
        })
        self._refresh_listbox()
        self._redraw()

    def _refresh_listbox(self):
        self.listbox.delete(0, "end")
        for roi in self.rois:
            self.listbox.insert(
                "end", f"{roi['name']}  (x={roi['x']:.0%}, y={roi['y']:.0%}, "
                       f"{roi['w']:.0%}×{roi['h']:.0%})")

    def _delete_roi(self):
        selection = self.listbox.curselection()
        if selection:
            del self.rois[selection[0]]
            self._refresh_listbox()
            self._redraw()

    # ---------------- 模板管理 ----------------
    def _refresh_combo(self):
        self.combo.configure(values=list(self.templates.keys()))
        if self.templates and not self.combo.get():
            self.combo.current(0)

    def _load_template(self):
        name = self.combo.get()
        if not name:
            messagebox.showinfo("提示", "请先选择模板。", parent=self.win)
            return
        import copy
        self.rois = [dict(r) for r in self.templates.get(name, [])]
        self._refresh_listbox()
        self._redraw()
        self.roi_status.set(f"已加载模板「{name}」，共 {len(self.rois)} 个区域")

    def _save_template(self):
        if not self.rois:
            messagebox.showwarning("提示", "还没有定义任何区域。", parent=self.win)
            return
        name = self.name_var.get().strip()
        if not name:
            messagebox.showwarning("提示", "请先填写模板名。", parent=self.win)
            return
        import copy
        self.templates[name] = [dict(r) for r in self.rois]
        main.save_roi_templates(self.templates)
        self._refresh_combo()
        self.combo.set(name)
        self.roi_status.set(f"模板「{name}」已保存（{len(self.rois)} 个区域）")

    def _delete_template(self):
        name = self.combo.get()
        if not name:
            return
        if messagebox.askyesno("确认", f"删除模板「{name}」?", parent=self.win):
            self.templates.pop(name, None)
            main.save_roi_templates(self.templates)
            self._refresh_combo()
            self.roi_status.set(f"模板「{name}」已删除")

    # ---------------- 试识别 / 批量识别 ----------------
    def _trial_run(self):
        if self._busy or self._rgb is None or not self.rois:
            messagebox.showinfo(
                "提示", "需要: 1) 已加载示例图 2) 至少一个区域。", parent=self.win)
            return
        self._submit(lambda ocr, pre, cor: self._trial_worker(ocr, pre, cor))

    def _trial_worker(self, ocr, pre, cor):
        import cv2
        import numpy as np
        data = np.fromfile(str(self._sample_path), dtype=np.uint8)
        bgr = cv2.imdecode(data, cv2.IMREAD_COLOR)
        return ("trial", main.run_roi_ocr(ocr, bgr, self.rois, pre, cor))

    def _start_batch(self):
        if self._busy:
            return
        if not self.rois:
            messagebox.showwarning("提示", "请先定义或加载 ROI 模板。",
                                   parent=self.win)
            return
        folder = Path(self.get_folder())
        if not folder.is_dir():
            messagebox.showwarning("提示", f"文件夹不存在:\n{folder}",
                                   parent=self.win)
            return
        self._submit(lambda ocr, pre, cor: self._batch_worker(
            ocr, pre, cor, folder))

    def _batch_worker(self, ocr, pre, cor, folder):
        import cv2
        import numpy as np
        images = sorted(p for p in folder.iterdir()
                        if p.suffix.lower() in main.IMG_EXTS)
        results = []
        for idx, path in enumerate(images, 1):
            self.queue.put(("progress", idx, len(images), path.name))
            try:
                data = np.fromfile(str(path), dtype=np.uint8)
                bgr = cv2.imdecode(data, cv2.IMREAD_COLOR)
                if bgr is None:
                    raise ValueError("图片解码失败")
                fields = main.run_roi_ocr(ocr, bgr, self.rois, pre, cor)
                results.append({
                    "image": str(path.relative_to(folder)),
                    "time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                    "fields": fields,
                })
            except Exception as exc:
                results.append({"image": str(path), "time": "",
                                "fields": {}, "error": str(exc)})
        return ("batch", results)

    def _submit(self, job):
        """job(ocr, preprocessor, corrector) -> ("trial"/"batch", 数据)"""
        self._busy = True
        self.trial_btn.configure(state="disabled")
        self.batch_btn.configure(state="disabled")
        self.status_var.set("正在加载 OCR 模型...")

        def worker():
            try:
                ocr = main.create_ocr()
                pre = main.ImagePreprocessor(deskew=self.roi_deskew_var.get())
                payload = job(ocr, pre, _get_corrector())
                self.queue.put(payload)
            except Exception as exc:
                self.queue.put(("error", str(exc)))

        threading.Thread(target=worker, daemon=True).start()

    def _poll_queue(self):
        try:
            while True:
                msg = self.queue.get_nowait()
                kind, payload = msg[0], msg[1] if len(msg) > 1 else None
                if kind == "progress":
                    _, idx, total, name = msg
                    self.status_var.set(f"识别中 {idx}/{total}: {name}")
                elif kind == "trial":
                    self._show_trial(payload)
                    self.status_var.set("试识别完成")
                elif kind == "batch":
                    self._show_batch_results(payload)
                    self.status_var.set("批量识别完成")
                elif kind == "error":
                    messagebox.showerror("失败", payload, parent=self.win)
                    self.status_var.set(f"失败: {payload}")
        except queue.Empty:
            pass
        finally:
            if self._busy:
                self._busy = False
                self.trial_btn.configure(state="normal")
                self.batch_btn.configure(state="normal")
        self.win.after(80, self._poll_queue)

    def _show_trial(self, fields: dict):
        top = tk.Toplevel(self.win)
        top.title("试识别结果")
        top.transient(self.win)
        text = tk.Text(top, width=64, height=14, font=FONT)
        text.pack(fill="both", expand=True, padx=10, pady=10)
        for name, field in fields.items():
            mark = "  [需人工复核]" if field["review"] else ""
            text.insert("end", f"{name}: {field['text'] or '（未识别到）'}"
                               f"　(置信度 {field['confidence']:.2f}){mark}\n")
        text.configure(state="disabled")
        ttk.Button(top, text="关闭", command=top.destroy).pack(pady=(0, 10))

    def _show_batch_results(self, results: list):
        top = tk.Toplevel(self.win)
        top.title("ROI 批量识别结果")
        top.geometry("1000x560")
        top.transient(self.win)

        field_names = []
        for result in results:
            for name in result.get("fields", {}):
                if name not in field_names:
                    field_names.append(name)

        wrap = ttk.Frame(top, padding=10)
        wrap.pack(fill="both", expand=True)
        columns = ("image",) + tuple(f"f{i}" for i in range(len(field_names)))
        tree = ttk.Treeview(wrap, columns=columns, show="headings")
        tree.heading("image", text="图片文件")
        tree.column("image", width=180, anchor="w")
        for i, name in enumerate(field_names):
            tree.heading(f"f{i}", text=name)
            tree.column(f"f{i}", width=150, anchor="w")
        vsb = ttk.Scrollbar(wrap, orient="vertical", command=tree.yview)
        tree.configure(yscrollcommand=vsb.set)
        tree.pack(side="left", fill="both", expand=True)
        vsb.pack(side="right", fill="y")
        tree.tag_configure("review", background="#FCE4EC")

        for result in results:
            values = [result["image"]] + [
                result.get("fields", {}).get(name, {}).get("text", "")
                for name in field_names]
            review = any(f.get("review")
                         for f in result.get("fields", {}).values())
            tree.insert("", "end", values=values,
                        tags=("review",) if review else ())

        def do_export():
            try:
                xlsx = main.export_roi_excel(results)
                main.save_log({
                    "run_time": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
                    "mode": "roi",
                    "template": self.combo.get() or self.name_var.get(),
                    "image_count": len(results),
                    "results": results,
                })
                messagebox.showinfo("完成", f"已导出:\n{xlsx}", parent=top)
            except Exception as exc:
                messagebox.showerror("导出失败", str(exc), parent=top)

        bar = ttk.Frame(top, padding=(10, 0, 10, 10))
        bar.pack(fill="x")
        ttk.Button(bar, text="导出 Excel", command=do_export).pack(side="left")
        ttk.Label(bar,
                  text=f"共 {len(results)} 张，淡红底为需人工复核").pack(
            side="left", padx=(12, 0))


if __name__ == "__main__":
    root = tk.Tk()
    OcrGui(root)
    root.mainloop()
