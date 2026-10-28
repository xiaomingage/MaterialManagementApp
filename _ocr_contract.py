"""OCR 提取合同扫描件（无文字层 PDF）。

流程：pypdfium2 渲染每页为高清位图 -> pytesseract(chi_sim) 识别 -> 输出全文。
"""
import pypdfium2 as pdfium
import pytesseract
from PIL import Image

PDF_PATH = r"c:\Users\xm\Desktop\新建文件夹\物资管理\采购合同\梧州地材采购合同-诚泰建筑工程有限公司2026-06- 04.pdf"
TESSDATA_DIR = r"C:\Users\xm\AppData\Local\tessdata"
TESSERACT_EXE = r"C:\Program Files\Tesseract-OCR\tesseract.exe"

pytesseract.pytesseract.tesseract_cmd = TESSERACT_EXE

pdf = pdfium.PdfDocument(PDF_PATH)
print(f"PAGES = {len(pdf)}")

for i in range(len(pdf)):
    page = pdf[i]
    # scale=3 约等于 216 DPI，扫描件识别小字需要较高分辨率
    bitmap = page.render(scale=3)
    img: Image.Image = bitmap.to_pil()
    # 转灰度可略微提升 tesseract 稳定性
    img = img.convert("L")
    text = pytesseract.image_to_string(
        img,
        lang="chi_sim",
        config=f"--tessdata-dir {TESSDATA_DIR} --psm 6",
    )
    print(f"\n===== PAGE {i+1} OCR =====")
    print(text)

pdf.close()
