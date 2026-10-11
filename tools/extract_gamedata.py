# -*- coding: utf-8 -*-
"""从 .reference/sts2-v0.111.0 反编译源码提取搜种器所需数据表 -> GameData.g.cs

假设账号为全解锁档案（所有 Epoch 已揭示、DiscoveredActs 全部发现），
与 docs/validation 里验证用的无头引擎存档一致。
"""
import re, os, glob
from pathlib import Path

REF = os.environ.get('STS2_REFERENCE', str(Path(__file__).resolve().parents[2] / '.reference' / 'sts2-v0.111.0'))
OUT = os.path.join(os.path.dirname(__file__), '..', 'src', 'GameData.g.cs')

def snake_upper(name: str) -> str:
    s = re.sub(r'(?<!^)(?=[A-Z0-9])', '_', name).upper()
    s = re.sub(r'_+', '_', s)
    return s

def read(p):
    return open(p, encoding='utf-8').read()

def find_one(pat, text, cast=str):
    m = re.search(pat, text)
    return cast(m.group(1)) if m else None

# ---------- 幕 ----------
ACTS = ['Overgrowth', 'Underdocks', 'Hive', 'Glory']
act_data = {}
for a in ACTS:
    src = read(f'{REF}/MegaCrit.Sts2.Core.Models.Acts/{a}.cs')
    d = {}
    d['index'] = find_one(r'public override int Index => (\d+)', src, int)
    d['rooms'] = find_one(r'BaseNumberOfRooms => (\d+)', src, int)
    d['weakCount'] = find_one(r'NumberOfWeakEncounters => (\d+)', src, int)
    d['events'] = re.findall(r'ModelDb\.Event<(\w+)>\(\)', src)
    enc_body = re.search(r'GenerateAllEncounters\(\)\s*\{?\s*return new .*?\[(\d+)\]\s*\{(.*?)\n\t\t\}\);', src, re.S)
    d['encounters'] = re.findall(r'ModelDb\.Encounter<(\w+)>\(\)', enc_body.group(2)) if enc_body else \
        re.findall(r'ModelDb\.Encounter<(\w+)>\(\)', src)
    d['ancients'] = re.findall(r'ModelDb\.AncientEvent<(\w+)>\(\)', src)
    # GetMapPointTypes
    body = re.search(r'GetMapPointTypes\(Rng mapRng\)\s*\{(.*?)\n\t\}', src, re.S).group(1)
    if 'NextGaussianInt' in body.split('unknownCount')[0]:
        d['rest'] = ('gauss',) + tuple(int(x) for x in re.findall(r'NextGaussianInt\((\d+), (\d+), (\d+), (\d+)\)', body)[0])
    else:
        d['rest'] = ('uniform',) + tuple(int(x) for x in re.findall(r'NextInt\((\d+), (\d+)\)', body)[0])
    d['unknown_minus'] = ('StandardRandomUnknownCount(mapRng) - 1' in body)
    act_data[a] = d

# ---------- 遭遇 ----------
enc_data = {}
for p in glob.glob(f'{REF}/MegaCrit.Sts2.Core.Models.Encounters/*.cs'):
    cls = os.path.basename(p)[:-3]
    if cls in ('EncounterModel', 'DeprecatedEncounter'):
        continue
    src = read(p)
    rt = find_one(r'public override RoomType RoomType => RoomType\.(\w+)', src)
    weak = find_one(r'public override bool IsWeak => (\w+)', src)
    tm = re.search(r'IEnumerable<EncounterTag> Tags =>(.*?);', src, re.S)
    tags = re.findall(r'EncounterTag\.(\w+)', tm.group(1)) if tm else []
    # Encounter groups expose every possible monster through AllPossibleMonsters.
    # Keep declaration order and remove duplicates so the UI can explain a
    # group without pretending that a random group has one fixed lineup.
    monsters = []
    for monster in re.findall(r'ModelDb\.Monster<(\w+)>\(\)', src):
        if monster not in monsters:
            monsters.append(monster)
    enc_data[cls] = {'roomType': rt, 'isWeak': weak is not None and weak.lower() == 'true',
                     'tags': tags, 'monsters': monsters}

# ---------- 卡池 ----------
CARD_EPOCH = {}  # epoch -> cards (全解锁档案不使用, 仅记录)
for p in glob.glob(f'{REF}/MegaCrit.Sts2.Core.Timeline.Epochs/*.cs'):
    cls = os.path.basename(p)[:-3]
    src = read(p)
    cards = re.findall(r'ModelDb\.Card<(\w+)>\(\)', src)
    if cards:
        CARD_EPOCH[cls] = cards

card_pools = {}
for p in glob.glob(f'{REF}/MegaCrit.Sts2.Core.Models.CardPools/*CardPool.cs'):
    cls = os.path.basename(p)[:-3]
    if cls in ('CardPoolModel', 'MockCardPool', 'DeprecatedCardPool', 'TokenCardPool', 'QuestCardPool'):
        continue
    src = read(p)
    body = re.search(r'GenerateAllCards\(\).{0,200}?\n\t\{(.*?)\n\t\}', src, re.S)
    text = body.group(1) if body else src
    cards = [c for c in re.findall(r'ModelDb\.Card<(\w+)>\(\)', text) if os.path.exists(f'{REF}/MegaCrit.Sts2.Core.Models.Cards/{c}.cs')]
    if cards:
        card_pools[cls] = cards

CARD_DIR = f'{REF}/MegaCrit.Sts2.Core.Models.Cards'
def card_rarity(c):
    src = read(f'{CARD_DIR}/{c}.cs')
    m = re.search(r'CardRarity\.(\w+)\s*,\s*TargetType', src)
    return m.group(1) if m else 'None'

# ---------- 药水池 ----------
POT_EPOCH = {}
for p in glob.glob(f'{REF}/MegaCrit.Sts2.Core.Timeline.Epochs/*.cs'):
    cls = os.path.basename(p)[:-3]
    src = read(p)
    pots = re.findall(r'ModelDb\.Potion<(\w+)>\(\)', src)
    if pots:
        POT_EPOCH[cls] = pots

potion_pools = {}
for p in glob.glob(f'{REF}/MegaCrit.Sts2.Core.Models.PotionPools/*PotionPool.cs'):
    cls = os.path.basename(p)[:-3]
    if cls in ('PotionPoolModel', 'MockPotionPool', 'DeprecatedPotionPool', 'TokenPotionPool', 'EventPotionPool'):
        continue
    src = read(p)
    body = re.search(r'GenerateAllPotions\(\)\s*\{?\s*(?:return)?(.*?);\s*\n\s*\}', src, re.S)
    text = body.group(1) if body else ''
    m = re.search(r'(\w+Epoch)\.Potions', text)
    if m:
        potions = POT_EPOCH[m.group(1)]
    else:
        potions = re.findall(r'ModelDb\.Potion<(\w+)>\(\)', text)
    potion_pools[cls] = potions

POT_DIR = f'{REF}/MegaCrit.Sts2.Core.Models.Potions'
def pot_rarity(c):
    src = read(f'{POT_DIR}/{c}.cs')
    return find_one(r'PotionRarity\.(\w+)', src)

# ---------- 共享事件 ----------
mdb = read(f'{REF}/MegaCrit.Sts2.Core.Models/ModelDb.cs')
block = mdb.split('AllSharedEvents =>')[1].split('AllSharedAncients')[0]
shared_events = [a or b for a, b in re.findall(r'ModelDb\.Event<(\w+)>\(\)|Event<(\w+)>\(\)', block)]

# ---------- 捏奥 ----------
neow = read(f'{REF}/MegaCrit.Sts2.Core.Models.Events/Neow.cs')
pos_block = neow.split('PositiveOptions =>')[1].split('\t});')[0]
curse_block = neow.split('CurseOptions =>')[1].split('\t});')[0]
neow_positive = re.findall(r'RelicOption<(\w+)>\(', pos_block)
neow_curse = re.findall(r'RelicOption<(\w+)>\(', curse_block)

# ---------- 多人限定卡 ----------
CARD_MP_DIR = f'{REF}/MegaCrit.Sts2.Core.Models.Cards'
card_mp_only = []
for c in allcards_list if (allcards_list := None) else []:
    pass
for p in glob.glob(f'{CARD_MP_DIR}/*.cs'):
    cls = os.path.basename(p)[:-3]
    src = read(p)
    if re.search(r'MultiplayerConstraint => CardMultiplayerConstraint\.MultiplayerOnly', src):
        card_mp_only.append(cls)
card_mp_only.sort()

# ---------- 遗物池 ----------
relic_pools = {}
for p in glob.glob(f'{REF}/MegaCrit.Sts2.Core.Models.RelicPools/*RelicPool.cs'):
    cls = os.path.basename(p)[:-3]
    if cls == 'RelicPoolModel':
        continue
    src = read(p)
    relics = [a or b for a, b in re.findall(r'ModelDb\.Relic<(\w+)>\(\)|Relic<(\w+)>\(\)', src)]
    if relics:
        relic_pools[cls] = relics

REL_DIR = f'{REF}/MegaCrit.Sts2.Core.Models.Relics'
def relic_rarity(c):
    src = read(f'{REL_DIR}/{c}.cs')
    m = re.search(r'RelicRarity Rarity => RelicRarity\.(\w+)', src)
    return m.group(1) if m else 'Common'

# ---------- 生成 C# ----------
def arr_str(items):
    return 'new[] { ' + ', '.join(f'"{i}"' for i in items) + ' }'

lines = []
lines.append('// <auto-generated by tools/extract_gamedata.py — do not edit />')
lines.append('namespace SeedSearch;')
lines.append('public static class GameData')
lines.append('{')
lines.append('    public static readonly string[] SharedEvents = ' + arr_str(shared_events) + ';')
lines.append('')
lines.append('    public static readonly ActDef[] Acts =')
lines.append('    {')
for a in ACTS:
    d = act_data[a]
    if d['rest'][0] == 'gauss':
        rest = f'HasRestGauss: true, RestGauss: new Int4({d["rest"][1]},{d["rest"][2]},{d["rest"][3]},{d["rest"][4]}), RestUniform: default'
    else:
        rest = f'HasRestGauss: false, RestGauss: default, RestUniform: new Int2({d["rest"][1]},{d["rest"][2]})'
    lines.append(f'        new("{a}", {d["index"]}, {d["rooms"]}, {d["weakCount"]}, {rest}, UnknownMinus: {"true" if d["unknown_minus"] else "false"},')
    lines.append(f'            Events: {arr_str(d["events"])},')
    lines.append(f'            Ancients: {arr_str(d["ancients"])},')
    lines.append('            Encounters: new EncDef[] {')
    for e in d['encounters']:
        ed = enc_data[e]
        tags = ', '.join(f'"{t}"' for t in ed['tags']) or ''
        monsters = ', '.join(f'"{m}"' for m in ed['monsters']) or ''
        lines.append(f'                new("{e}", "{ed["roomType"]}", Weak: {"true" if ed["isWeak"] else "false"}, Tags: new string[] {{ {tags} }}, Monsters: new string[] {{ {monsters} }}),')
    lines.append('            }),')
lines.append('    };')
lines.append('')
lines.append('    public static readonly Dictionary<string, string[]> CardPools = new()')
lines.append('    {')
for k, v in card_pools.items():
    lines.append(f'        ["{k}"] = {arr_str(v)},')
lines.append('    };')
allcards = sorted({c for v in card_pools.values() for c in v})
lines.append('    public static readonly Dictionary<string, string> CardRarity = new()')
lines.append('    {')
for c in allcards:
    lines.append(f'        ["{c}"] = "{card_rarity(c)}",')
lines.append('    };')
lines.append('    public static readonly Dictionary<string, string[]> PotionPools = new()')
lines.append('    {')
for k, v in potion_pools.items():
    lines.append(f'        ["{k}"] = {arr_str(v)},')
lines.append('    };')
allpots = sorted({c for v in potion_pools.values() for c in v})
lines.append('    public static readonly Dictionary<string, string> PotionRarity = new()')
lines.append('    {')
for c in allpots:
    lines.append(f'        ["{c}"] = "{pot_rarity(c)}",')
lines.append('    };')
lines.append('    public static readonly string[] NeowPositive = ' + arr_str(neow_positive) + ';')
lines.append('    public static readonly string[] NeowCurse = ' + arr_str(neow_curse) + ';')
lines.append('    public static readonly HashSet<string> CardMultiplayerOnly = new() { ' + ', '.join(f'"{c}"' for c in card_mp_only) + ' };')
lines.append('    public static readonly Dictionary<string, string[]> RelicPools = new()')
lines.append('    {')
for k, v in relic_pools.items():
    lines.append(f'        ["{k}"] = {arr_str(v)},')
lines.append('    };')
allrelics = sorted({c for v in relic_pools.values() for c in v})
lines.append('    public static readonly Dictionary<string, string> RelicRarity = new()')
lines.append('    {')
for c in allrelics:
    lines.append(f'        ["{c}"] = "{relic_rarity(c)}",')
lines.append('    };')
lines.append('}')
os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, 'w', encoding='utf-8').write('\n'.join(lines))
print('written', OUT)
print('acts:', {a: (act_data[a]['index'], act_data[a]['rooms'], act_data[a]['weakCount'], len(act_data[a]['events']), len(act_data[a]['encounters']), act_data[a]['rest'], act_data[a]['unknown_minus']) for a in ACTS})
print('pools:', {k: len(v) for k, v in card_pools.items()})
print('potion pools:', {k: len(v) for k, v in potion_pools.items()})
print('neow pos/curse:', len(neow_positive), len(neow_curse))
