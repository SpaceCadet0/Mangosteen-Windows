"""Builds src/Mangosteen.Core/Strings/<language>.json from every L("…") string in the sources.

Languages: ru, es (translations-windows.py), de, fr, pt-BR, zh-Hans, ja (translations-more.py), and strings added with
the port of the macOS app's 0.10.4-0.10.7 features in all seven (translations-new.py). The macOS app's
translations.json (path in MANGOSTEEN_MAC_TRANSLATIONS or ../Mangosteen App/scripts/translations.json) fills in the
rest. Fails when a string has no translation in some language, or when placeholders differ.
"""
import glob, json, os, re, sys

root = os.path.join(os.path.dirname(__file__), '..')
LANGUAGES = ['ru', 'es', 'de', 'fr', 'pt-BR', 'zh-Hans', 'ja']
exec(open(os.path.join(root, 'build', 'translations-windows.py'), encoding='utf-8').read())
exec(open(os.path.join(root, 'build', 'translations-more.py'), encoding='utf-8').read())
exec(open(os.path.join(root, 'build', 'translations-new.py'), encoding='utf-8').read())
mac_path = os.environ.get('MANGOSTEEN_MAC_TRANSLATIONS', os.path.join(root, '..', 'Mangosteen App', 'scripts', 'translations.json'))
mac = json.load(open(mac_path, encoding='utf-8'))
from_mac = {}
for ru, values in mac.items():
    if 'en' in values and '%' not in ru:
        entry = dict(values)
        entry['ru'] = ru
        from_mac.setdefault(values['en'], entry)

strings = set()
for path in glob.glob(os.path.join(root, 'src', '**', '*.cs'), recursive=True):
    text = open(path, encoding='utf-8').read()
    strings.update(m.group(1) for m in re.finditer(r'\bL\("((?:[^"\\]|\\.)*)"\)', text))
    if path.endswith('WheelCatalog.cs'):
        strings.update(m.group(1) for m in re.finditer(r'Add\([^,]+, "tool\.\w+", "([^"]+)"', text))
        strings.update(m.group(1) for m in re.finditer(r'new\("action\.\w+", "([^"]+)"', text))
        strings.update(m.group(1) for m in re.finditer(r'\("tool\.\w+", "([^"]+)", WheelIcon', text))
    if path.endswith('WhatsNewWindow.cs'):
        for m in re.finditer(r'\("[^"]*", "((?:[^"\\]|\\.)+)", "((?:[^"\\]|\\.)+)"\)', text):
            strings.update([m.group(1), m.group(2)])
    if path.endswith('Accent.cs'):
        strings.update(m.group(1) for m in re.finditer(r'\("#?[0-9A-Fa-f]*", "([^"]+)"\)', text))
# Strings passed to L() through variables (editor sliders, effects, annotate tools, aspect choices, loudness targets).
strings.update(["Light", "Colour", "Detail", "Effects", "None", "Mono", "Noir", "Tonal", "Chrome", "Fade", "Instant", "Process",
                "Transfer", "Exposure", "Brightness", "Contrast", "Highlights", "Shadows", "Temperature", "Tint", "Saturation",
                "Vibrance", "Sharpness", "Clarity", "Noise reduction", "Dehaze", "Vignette", "Grain", "Free", "Select", "Arrow",
                "Rectangle", "Oval", "Text", "Number", "Pencil", "Highlighter", "Magnifier", "Picture",
                "Streaming and music (−14 LUFS)", "Podcast (−16 LUFS)", "TV and radio (−23 LUFS)"])

def unescape(key):
    return key.replace('\\u201C', '“').replace('\\u201D', '”').replace('\\"', '"')

windows = {unescape(k): v for k, v in T.items()}      # (ru, es)
more = {unescape(k): v for k, v in M.items()}          # (de, fr, pt-BR, zh-Hans, ja)
new = {unescape(k): v for k, v in N.items()}           # (ru, es, de, fr, pt-BR, zh-Hans, ja)
tables = {code: {} for code in LANGUAGES}
missing = []
for s in sorted(strings):
    key = unescape(s)
    for index, code in enumerate(LANGUAGES):
        value = None
        if key in new:
            value = new[key][index]
        elif code in ('ru', 'es') and key in windows:
            value = windows[key][index]
        elif key in more and code not in ('ru', 'es'):
            value = more[key][index - 2]
        elif key in from_mac and from_mac[key].get(code):
            value = from_mac[key][code]
        if value is None:
            missing.append(f'{code}: {key}')
        else:
            tables[code][key] = value
if missing:
    sys.exit('No translation for:\n' + '\n'.join(missing))
for code, table in tables.items():
    for key, value in table.items():
        if sorted(re.findall(r'\{\d[^}]*\}', key)) != sorted(re.findall(r'\{\d[^}]*\}', value)):
            sys.exit(f'{code}: placeholders differ in "{key}"')
    with open(os.path.join(root, 'src', 'Mangosteen.Core', 'Strings', f'{code}.json'), 'w', encoding='utf-8') as out:
        json.dump(table, out, ensure_ascii=False, indent=1, sort_keys=True)
print(f'{len(strings)} strings translated into {len(LANGUAGES)} languages')
