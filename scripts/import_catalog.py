"""Read the supplied workbook using the Python standard library; no Excel edits.
Usage: python scripts/import_catalog.py path/to/workbook.xlsx
"""
import sys
import json
import zipfile
import xml.etree.ElementTree as ET
from pathlib import Path

ns = {"s": "http://schemas.openxmlformats.org/spreadsheetml/2006/main"}
with zipfile.ZipFile(sys.argv[1]) as archive:
    strings = ["".join(n.itertext()) for n in ET.fromstring(archive.read("xl/sharedStrings.xml"))]
    cells = {}
    for cell in ET.fromstring(archive.read("xl/worksheets/sheet1.xml")).findall(".//s:c", ns):
        value = cell.find("s:v", ns)
        if value is not None:
            cells[cell.get("r")] = (strings[int(value.text)] if cell.get("t") == "s" else value.text).strip()

rows = []
def add(key, kind, label, parent=None, requires=False, reserve=False):
    rows.append(dict(Key=key, Kind=kind, Label=label, Parent=parent, RequiresDescription=requires, RequiresReserve=reserve))

for i in [*range(1, 15), 22, 42, 43, 47, 49]: add(f"site{i}", "site", f"ВДВП №{i}")
for i in range(1, 3): add(f"shift{i}", "shift", str(i))
for i in range(5, 8): add(f"person{i}", "person", cells[f"F{i}"])
for i in range(1, 5): add(f"machine{i}", "machine", f"APOLLO №{i}")
for i, label in enumerate(["Самостійно", "Залучення РМД", "Залучення АСКВ", "Залучення логістики"]):
    add(f"repair{i}", "repairMethod", label, reserve=i > 0)
parts = [
    ("J", "Жатка", [14, 24, 29, 34, 40, 43, 52, 59], 62),
    ("K", "Причіп", [14, 18, 21, 30, 35, 40, 44], 49),
    ("L", "Машина", [14, 19, 25, 30, 35, 39, 57, 63], 66),
]
duplicates = 0
for col, part, headings, end in parts:
    add(col, "part", part)
    for start, stop in zip(headings, headings[1:] + [end]):
        node = f"{col}{start}"
        add(node, "node", cells[node], col)
        seen = set()
        for i in range(start + 1, stop):
            label = cells.get(f"{col}{i}")
            if not label: continue
            if label in seen:
                duplicates += 1
                continue
            seen.add(label)
            add(f"{col}{i}", "fault", label, node)
        add(node + "other", "fault", "Інша несправність", node, True)
add("other", "part", "Інше", requires=True)
add("othernode", "node", "Інше", "other", True)
add("otherfault", "fault", "Інша несправність", "othernode", True)
target = Path(__file__).resolve().parents[1] / "src/ApolloBreakdowns/Data/catalog.json"
target.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Imported {len(rows)} options; removed {duplicates} exact duplicate(s) within the same node.")
