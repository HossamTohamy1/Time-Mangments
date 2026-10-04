"""Builds nested en/ar JSON translation files from flat (en, ar) dictionaries, guaranteeing key parity."""
import importlib.util, json, os, sys

HERE = os.path.dirname(__file__)
OUT = os.path.join(HERE, '..', '..', 'public', 'i18n')

def nest(flat):
    root = {}
    for key, value in sorted(flat.items()):
        node = root
        parts = key.split('.')
        for p in parts[:-1]:
            node = node.setdefault(p, {})
            if not isinstance(node, dict): raise SystemExit(f'key conflict at {key}')
        node[parts[-1]] = value
    return root

def write(scope, table):
    d = os.path.join(OUT, scope) if scope else OUT
    os.makedirs(d, exist_ok=True)
    for i, lang in enumerate(['en', 'ar']):
        with open(os.path.join(d, f'{lang}.json'), 'w', encoding='utf-8') as f:
            json.dump(nest({k: v[i] for k, v in table.items()}), f, ensure_ascii=False, indent=2)
            f.write('\n')

def load(name):
    spec = importlib.util.spec_from_file_location(name, os.path.join(HERE, f'{name}.py'))
    m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m); return m.T

write('', load('root'))
for scope in [f[:-3] for f in os.listdir(HERE) if f.endswith('.py') and f not in ('root.py', 'build.py')]:
    write(scope, load(scope))
print('i18n files written')
