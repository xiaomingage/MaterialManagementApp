"""提取合同 PDF 的文本和表格，用于分析价格表结构。"""
import pdfplumber

pdf_path = r"c:\Users\xm\Desktop\新建文件夹\物资管理\采购合同\梧州地材采购合同-诚泰建筑工程有限公司2026-06- 04.pdf"

with pdfplumber.open(pdf_path) as pdf:
    print(f"PAGES = {len(pdf.pages)}")
    for i, page in enumerate(pdf.pages):
        text = page.extract_text() or ""
        print(f"\n===== PAGE {i+1} TEXT =====")
        print(text[:5000])
        print(f"\n--- PAGE {i+1} TABLES ---")
        tables = page.extract_tables()
        for j, t in enumerate(tables):
            print(f"  Table {j+1}: {len(t)} rows")
            for row in t[:25]:
                cells = [str(c) if c is not None else "" for c in row]
                print("    | " + " | ".join(cells))
            print()
