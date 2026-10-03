"""Generate the Word test-document set as Mac RTF (\\mac charset) plus a spec file.

Each RTF is opened in Word (3.01 / 4 / 5.1a / 6 / 98) and saved in that version's native format.
All content here is our own (synthetic), so the resulting files can be committed as fixtures.
"""
import binascii, json, os

OUT = os.path.dirname(os.path.abspath(__file__))

# Mac Roman escapes for RTF (\'hh in the \mac charset).
def mr(s):
    out = []
    for ch in s:
        b = ch.encode('mac_roman')
        if len(b) != 1:
            raise ValueError(ch)
        c = b[0]
        if ch in '\\{}':
            out.append('\\' + ch)
        elif c >= 0x80:
            out.append("\\'%02x" % c)
        else:
            out.append(ch)
    return ''.join(out)

HEAD = (r"{\rtf1\mac\deff2"
        r"{\fonttbl{\f2\froman New York;}{\f20\froman Times;}{\f3\fswiss Geneva;}}"
        r"{\colortbl;\red0\green0\blue0;\red255\green0\blue0;}"
        r"{\stylesheet{\f2\fs24 \snext0 Normal;}{\s1\sb240\sa60\keepn\b\f3\fs28 \sbasedon0\snext0 heading 1;}}"
        "\n")
PARD = r"\pard\plain\f2\fs24 "

def doc(body):
    return HEAD + body + "}\n"

# 1. plain
PLAIN = [
    "Plain text test document.",
    "The quick brown fox jumps over the lazy dog 0123456789.",
    "Mac Roman: é ü ß • ™ © “quoted” – en — em.",
]
plain_body = ''.join(PARD + mr(p) + r"\par" + "\n" for p in PLAIN)
plain_body += PARD + r"Tab:\tab after tab. Line one\line line two.\par" + "\n"

# 2. formats: one paragraph per format so each run is easy to check.
FORMATS = [
    ("Normal run.", ""),
    ("Bold run.", r"\b "),
    ("Italic run.", r"\i "),
    ("Underline run.", r"\ul "),
    ("Outline run.", r"\outl "),
    ("Shadow run.", r"\shad "),
    ("Small caps run.", r"\scaps "),
    ("All caps run.", r"\caps "),
    ("Hidden run.", r"\v "),
    ("Times run.", r"\f20 "),
    ("Geneva run.", r"\f3 "),
    ("Size 9 run.", r"\fs18 "),
    ("Size 12 run.", r"\fs24 "),
    ("Size 24 run.", r"\fs48 "),
    ("Red run.", r"\cf2 "),
]
fmt_body = ''
for text, ctl in FORMATS:
    fmt_body += PARD + "Format: {" + ctl + mr(text) + r"}\par" + "\n"
# one mixed paragraph with several runs in a row
fmt_body += PARD + r"Mixed: {\b bold} {\i italic} {\ul underline} {\b\i bold-italic} end.\par" + "\n"

# 3. paragraphs (twips: 0.5" = 720, 1" = 1440, 6 pt = 120, 12 pt = 240)
PARAS = [
    (r"\s1\sb240\sa60\keepn\b\f3\fs28 ", "Heading One", "style heading 1"),
    (r"\ql ", "Left aligned paragraph.", "left"),
    (r"\qc ", "Centered paragraph.", "center"),
    (r"\qr ", "Right aligned paragraph.", "right"),
    (r"\qj ", "Justified paragraph with enough words in it to wrap onto a second line so that the justification can be seen in the output of the reader.", "justified"),
    (r"\li720 ", "Left indent 0.5 inch.", "left indent 720 twips"),
    (r"\ri1440 ", "Right indent 1 inch with enough text to wrap onto a second line before the right margin is reached here.", "right indent 1440 twips"),
    (r"\fi720 ", "First line indent 0.5 inch with enough text to wrap onto a second line of this paragraph.", "first-line indent 720 twips"),
    (r"\fi-720\li720 ", "Hanging indent 0.5 inch with enough text to wrap onto a second line of this paragraph.", "left 720, first -720 twips"),
    (r"\sb120\sa240 ", "Space before 6 pt and after 12 pt.", "space before 120, after 240 twips"),
    ("", "Normal paragraph after.", "normal"),
]
par_body = ''
for ctl, text, _ in PARAS:
    if ctl.startswith(r"\s1"):
        par_body += r"\pard\plain" + ctl + mr(text) + r"\par" + "\n"
    else:
        par_body += r"\pard\plain" + ctl + r"\f2\fs24 " + mr(text) + r"\par" + "\n"

# 4. table 3x3
tbl_body = PARD + r"Table follows.\par" + "\n"
for r in range(3):
    tbl_body += r"\trowd\trgaph108\trleft-108\cellx2880\cellx5760\cellx8640" + "\n"
    tbl_body += r"\pard\plain\intbl\f2\fs24 "
    tbl_body += ''.join("%s%d\\cell " % ("ABC"[c], r + 1) for c in range(3))
    tbl_body += r"\pard\plain\intbl\row" + "\n"
tbl_body += PARD + r"Text after table.\par" + "\n"

# 5. picture + footnote
pict = open(os.path.join(OUT, 'Tile.pict'), 'rb').read()[512:]
hexd = binascii.hexlify(pict).decode()
hexd = '\n'.join(hexd[i:i + 128] for i in range(0, len(hexd), 128))
pic_body = PARD + r"Picture follows:\par" + "\n"
pic_body += PARD + r"{\pict\macpict\picw64\pich64\picwgoal1280\pichgoal1280" + "\n" + hexd + r"}\par" + "\n"
pic_body += PARD + r"Text with a footnote{\super\chftn}{\footnote\pard\plain\f2\fs20 {\super\chftn} This is the footnote.} here.\par" + "\n"
pic_body += PARD + r"Last paragraph.\par" + "\n"

DOCS = {'plain': plain_body, 'formats': fmt_body, 'paragraphs': par_body, 'table': tbl_body, 'picture+footnote': pic_body}
for name, body in DOCS.items():
    fn = name.replace('+', '_') + '.rtf'
    open(os.path.join(OUT, 'rtf', fn), 'w', encoding='ascii', newline='\r').write(doc(body))

spec = {
    'note': 'Our own synthetic content. Source RTF opened in each Word version and saved natively; *-fast variants got the edits listed under fast_edits, then a fast save.',
    'default_font': 'New York 12 (Normal)',
    'plain': {'paragraphs': PLAIN + ['Tab:<TAB>after tab. Line one<LINE BREAK (Word new-line char)>line two.']},
    'formats': [{'paragraph': 'Format: ' + t, 'run': t, 'rtf': c.strip() or 'none'} for t, c in FORMATS]
               + [{'paragraph': 'Mixed: bold italic underline bold-italic end.', 'runs': {'bold': 'b', 'italic': 'i', 'underline': 'ul', 'bold-italic': 'b+i'}}],
    'paragraphs': [{'text': t, 'format': f} for _, t, f in PARAS],
    'table': {'before': 'Table follows.', 'cells': [['A1', 'B1', 'C1'], ['A2', 'B2', 'C2'], ['A3', 'B3', 'C3']], 'col_right_edges_twips': [2880, 5760, 8640], 'after': 'Text after table.'},
    'picture+footnote': {'paragraphs': ['Picture follows:', '<inline PICT 64x64, Tile.pict from FolderArt2 fixture, shown at 64x64 pt>', 'Text with a footnote<FOOTNOTE REF 1> here.', 'Last paragraph.'],
                         'footnote': '<REF 1> This is the footnote.'},
    'fast_edits': 'In each *-fast file: (1) after the first word of the 2nd paragraph insert " EDITED-MIDDLE"; (2) at the very end of the last paragraph insert " EDITED-END". Then save with Fast Save on.',
}
json.dump(spec, open(os.path.join(OUT, 'spec.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print('ok')
