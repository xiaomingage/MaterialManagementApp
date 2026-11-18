"""OCR 提取合同扫描件（无文字层 PDF）。

流程：pypdfium2 渲染每页为高清位图 -> pytesseract(chi_sim) 识别 -> 输出全文。

用法：
    python _ocr_contract.py <合同 PDF 路径>

可选环境变量：
    TESSDATA_DIR  tessdata 目录；不设置时使用 tesseract 默认语言包位置。
"""
import os
import sys

import pypdfium2 as pdfium
import pytesseract
from PIL import Image

if len(sys.argv) < 2:
    sys.exit("用法：python _ocr_contract.py <合同 PDF 路径>")

PDF_PATH = sys.argv[1]
TESSDATA_DIR = os.environ.get("TESSDATA_DIR", "").strip()
OCR_CONFIG = "--psm 6" if not TESSDATA_DIR else f"--tessdata-dir {TESSDATA_DIR} --psm 6"

if not os.path.isfile(PDF_PATH):
    sys.exit(f"文件不存在：{PDF_PATH}")

pdf = pdfium.PdfDocument(PDF_PATH)
print(f"PAGES = {len(pdf)}")

for i in range(len(pdf)):
    page = pdf[i]
    # scale=3 约等于 216 DPI，扫描件识别小字需要较高分辨率
    bitmap = page.render(scale=3)
    img: Image.Image = bitmap.to_pil()
    # 转灰度可略微提升 tesseract 稳定性
    img = img.convert("L")
    text = pytesseract.image_to_string(img, lang="chi_sim", config=OCR_CONFIG)
    print(f"\n===== PAGE {i+1} OCR =====")
    print(text)

pdf.close()
