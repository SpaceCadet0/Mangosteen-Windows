"""Builds src/Mangosteen.Core/Strings/{ru,es}.json from every L("…") string in the sources.

Translations come from build/translations-windows.py first, then from the macOS app's translations.json
(path in MANGOSTEEN_MAC_TRANSLATIONS or ../Mangosteen App/scripts/translations.json). Fails when a string has none.
"""
import glob, json, os, re, sys

root = os.path.join(os.path.dirname(__file__), '..')
exec(open(os.path.join(root, 'build', 'translations-windows.py'), encoding='utf-8').read())
mac_path = os.environ.get('MANGOSTEEN_MAC_TRANSLATIONS', os.path.join(root, '..', 'Mangosteen App', 'scripts', 'translations.json'))
mac = json.load(open(mac_path, encoding='utf-8'))
from_mac = {}
for ru, values in mac.items():
    if 'en' in values and '%' not in ru:
        from_mac.setdefault(values['en'], (ru, values.get('es') or values['en']))

strings = set()
for path in glob.glob(os.path.join(root, 'src', '**', '*.cs'), recursive=True):
    text = open(path, encoding='utf-8').read()
    strings.update(m.group(1) for m in re.finditer(r'\bL\("((?:[^"\\]|\\.)*)"\)', text))
    if path.endswith('WheelCatalog.cs'):
        strings.update(m.group(1) for m in re.finditer(r'Add\([^,]+, "tool\.\w+", "([^"]+)"', text))
        strings.update(m.group(1) for m in re.finditer(r'new\("action\.\w+", "([^"]+)"', text))
# Strings passed to L() through variables (editor sliders, effects, annotate tools, aspect choices, loudness targets).
strings.update(["Light", "Colour", "Detail", "Effects", "None", "Mono", "Noir", "Tonal", "Chrome", "Fade", "Instant", "Process",
                "Transfer", "Exposure", "Brightness", "Contrast", "Highlights", "Shadows", "Temperature", "Tint", "Saturation",
                "Vibrance", "Sharpness", "Clarity", "Noise reduction", "Dehaze", "Vignette", "Grain", "Free", "Select", "Arrow",
                "Rectangle", "Oval", "Text", "Number", "Pencil", "Highlighter", "Magnifier", "Picture",
                "Streaming and music (−14 LUFS)", "Podcast (−16 LUFS)", "TV and radio (−23 LUFS)"])

def unescape(key):
    return key.replace('\\u201C', '“').replace('\\u201D', '”')

own = {unescape(k): v for k, v in T.items()}
ru, es, missing = {}, {}, []
for s in sorted(strings):
    key = unescape(s)
    if key in own:
        ru[key], es[key] = own[key]
    elif key in from_mac:
        ru[key], es[key] = from_mac[key]
    else:
        missing.append(key)
if missing:
    sys.exit('No translation for: ' + ' | '.join(missing))
for code, table in (('ru', ru), ('es', es)):
    for key, value in table.items():
        if sorted(re.findall(r'\{\d[^}]*\}', key)) != sorted(re.findall(r'\{\d[^}]*\}', value)):
            sys.exit(f'{code}: placeholders differ in "{key}"')
    with open(os.path.join(root, 'src', 'Mangosteen.Core', 'Strings', f'{code}.json'), 'w', encoding='utf-8') as out:
        json.dump(table, out, ensure_ascii=False, indent=1)
print(f'{len(ru)} strings translated')
