---
name: pdf
description: 处理 PDF 文件。用于读取、提取、创建或合并 PDF 文档。
---

# PDF 处理技能

本技能提供 PDF 文件处理的最佳实践和常用工具指南。

## 读取 PDF 文本

### 快速提取（推荐）

使用 `pdftotext`（poppler-utils 的一部分）快速提取纯文本：

```bash
# 安装（Ubuntu/Debian）
sudo apt install poppler-utils

# 安装（macOS）
brew install poppler

# 提取到标准输出
pdftotext input.pdf -

# 提取到文件
pdftotext input.pdf output.txt

# 保留布局
pdftotext -layout input.pdf output.txt
```

### 保留结构（复杂文档）

对于需要保留格式的文档，使用 PyMuPDF：

```python
import fitz  # pip install PyMuPDF

doc = fitz.open("input.pdf")
for page in doc:
    text = page.get_text()
    print(text)
```

## 提取表格数据

### 使用 tabula-py

```bash
pip install tabula-py
```

```python
import tabula

# 提取所有页面的表格
tables = tabula.read_pdf("input.pdf", pages='all')

# 保存为 CSV
for i, table in enumerate(tables):
    table.to_csv(f"table_{i}.csv", index=False)
```

### 使用 camelot（更精确）

```bash
pip install camelot-py[cv]
```

```python
import camelot

tables = camelot.read_pdf("input.pdf", pages='1-end')
tables.export("tables.csv", f='csv')
```

## 创建 PDF

### 从 Markdown 创建

```bash
# 使用 pandoc
pandoc input.md -o output.pdf

# 使用 md-to-pdf
npx md-to-pdf input.md
```

### 从 HTML 创建

```bash
# 使用 wkhtmltopdf
wkhtmltopdf input.html output.pdf

# 使用 Chrome headless
chrome --headless --print-to-pdf=output.pdf input.html
```

### 编程创建（C#）

```csharp
// 使用 QuestPDF（推荐）
// dotnet add package QuestPDF

using QuestPDF.Fluent;
using QuestPDF.Helpers;

Document.Create(container =>
{
    container.Page(page =>
    {
        page.Content().Text("Hello, PDF!");
    });
}).GeneratePdf("output.pdf");
```

## 合并 PDF

### 使用 pdftk

```bash
# 安装
sudo apt install pdftk  # Linux
brew install pdftk-java  # macOS

# 合并
pdftk file1.pdf file2.pdf cat output merged.pdf
```

### 使用 PyMuPDF

```python
import fitz

result = fitz.open()
for pdf_file in ["file1.pdf", "file2.pdf"]:
    doc = fitz.open(pdf_file)
    result.insert_pdf(doc)
result.save("merged.pdf")
```

## 拆分 PDF

```bash
# 按页拆分
pdftk input.pdf burst output page_%02d.pdf

# 提取特定页
pdftk input.pdf cat 1-5 output first_five.pdf
```

## 常见问题

### PDF 是扫描件怎么办？

使用 OCR 工具：

```bash
# 使用 Tesseract
tesseract input.png output pdf

# 使用 OCRmyPDF（推荐，保留原文档结构）
pip install ocrmypdf
ocrmypdf input.pdf output.pdf
```

### 如何压缩 PDF？

```bash
# 使用 Ghostscript
gs -sDEVICE=pdfwrite -dCompatibilityLevel=1.4 -dPDFSETTINGS=/ebook \
   -dNOPAUSE -dQUIET -dBATCH -sOutputFile=output.pdf input.pdf
```

### 如何加密 PDF？

```bash
# 使用 pdftk
pdftk input.pdf output secured.pdf user_pw password
```
