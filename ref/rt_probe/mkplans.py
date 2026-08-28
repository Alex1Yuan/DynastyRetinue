# -*- coding: utf-8 -*-
"""
离线抽取加点方案 -> KgdRetinue 自带的 plans.json

为什么要有这一步：
  mod 要能独立发布、开新档就能用。运行时**不能**去读玩家的存档，
  也不能依赖玩家装了 RTAutoBuilder 并且存了跟作者一样的方案。
  所以把两个素材源（作者的 55 级存档 + 作者在 RTAutoBuilder 里手搭的方案）
  在开发期抽一次，烘成 mod 自己的数据文件。

素材源的取舍：
  - 存档 m_Selections 是**结果**：真人跑到 55 级的完整记录，(Path, Level=rank, Feature)。
    优点是齐、真、无歧义；缺点是只有存档里出现过的职业链。
  - RTAutoBuilder 方案是**意图**：作者手搭的，覆盖存档里没有的链（比如先锋 Vanguard）。

用法:
  py mkplans.py            # 写到 src 和已部署目录
  py mkplans.py --dry      # 只打印统计
"""
import json, os, sys, zipfile, collections

SAVE = r'C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\Saved Games\Manual_24___________15_38_27.zks'
AUTOB = r'C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\RTAutoBuilder\AutoBuilderSettings.json'
CATALOG = r'D:\RT_RetinueMod\ref\bbp\catalog.tsv'
OUT = [r'D:\RT_RetinueMod\src\DynastyRetinue\plans.json',
       r'C:\Users\kyua805\AppData\LocalLow\Owlcat Games\Warhammer 40000 Rogue Trader\UnityModManager\DynastyRetinue\plans.json']

HOME_SEL = '10fefb03369d430e88b65aabaf68deac'   # HomeworldSelection
OCC_SEL  = 'ff001e095e7240ac99b40ceb2bdadf0a'   # OccupationSelection
CHARGEN_PATHS = ('ChargenPath_ForPregens', 'CustomCharacterChargenPath')

# 存档里要抽的角色 -> (稳定 id, 显示名)
FROM_SAVE = {
    'StartGame_Player_Unit':  ('mc_fighter_executioner',  '主控 · 战士行刑者'),
    'HeinrixCompanion':       ('heinrix_fighter_assassin', '海因里希 · 战士刺客'),
    'YrlietCompanion':        ('yrliet_adept_hunter',      '伊莉耶特 · 专家猎首'),
    'ArgentaCompanion':       ('argenta_soldier_veteran',  '阿杰塔 · 士兵首席战士'),
    'AbelardCompanion':       ('abelard_fighter_veteran',  '阿贝拉德 · 战士首席战士'),
    'CassiaCompanion':        ('cassia_leader_strategist', '卡西娅 · 领袖战略家'),
    'EogannCompanion':        ('eogann_leader_tactician',  '伊奥甘 · 领袖战术家'),
    'UlfarCompanion':         ('ulfar_soldier_veteran',    '乌尔法 · 士兵首席战士'),
    'PascalCompanion':        ('pascal_adept_assassin',    '帕斯卡 · 专家刺客'),
    'IdiraCompanion':         ('idira_adept_assassin',     '伊迪拉 · 专家刺客'),
    # ★基贝拉不从存档抽★ 作者存档里她的飞升只点到 rank15（另外五个都到 19），
    #   于是收割者/战术家/飞升三条路里，飞升 16-19 整段缺失 = 8 个选择点。
    #   两个近战精英（锈行者/电僧）用的正是这份方案，实测 84 个点里 8 个走回退，
    #   回退到第③档「无偏好」就是取第一个能选的 —— 点出了链锯武器专家（它们拿的是
    #   Primitive 族武器，根本吃不到）这种废点。
    #   RTAutoBuilder 里作者手搭的「战术DLC3」是完整的（飞升到 19），所以改从那边取，
    #   见下面的 FROM_AUTOB。
    'BoardedShip_Arbites_Clayton_Pregen':
                              ('arbites_soldier_hunter',   '法务官 · 士兵猎首（24级·不完整）'),
    'BoardedShip_Arbites_Bryce_Pregen':
                              ('arbites_fighter_vanguard', '法务官 · 战士先锋（24级·不完整）'),
}

# RTAutoBuilder 里作者手搭的方案 -> (稳定 id, 显示名)。按 BuildComment 前缀匹配。
FROM_AUTOB = [
    ('国教士兵首席连射',   'rt_soldier_suppress',    '国教士兵 · 首席连射'),
    ('灵能军官（辅助）',   'rt_psyker_officer',      '灵能军官（辅助）'),
    ('政委军官（辅助）',   'rt_commissar_officer',   '政委军官（辅助）'),
    ('火杖战士行刑者',     'rt_fire_executioner',    '火杖战士 · 行刑者'),
    ('先锋阿贝拉德',       'rt_abelard_vanguard',    '阿贝拉德 · 先锋'),
    # id 与显示名跟原来从存档抽的那份保持一致 —— archetypes.json 里两个近战精英
    # 写的就是 kibellah_reaper_tactician，改了 id 会让它们静默掉回"无方案"。
    ('战术DLC3',           'kibellah_reaper_tactician', '基贝拉 · 收割者战术家'),
]


def load_catalog():
    cat = {}
    with open(CATALOG, encoding='utf-8', errors='replace') as f:
        for ln in f:
            p = ln.rstrip('\n').split('\t')
            if len(p) > 1:
                cat[p[0]] = p[1]
    return cat


def selection_group_table():
    """Selection 蓝图 GUID -> FeatureGroup 名。

    存档的 m_Selections 只记了 Selection 蓝图，没记 FeatureGroup；
    而 RTAutoBuilder 的方案两样都有。用它当查表，就能给存档抽出来的条目补上组名。
    组名是按段拼方案时分桶用的（FirstCareer* / SecondCareer* 是相对职业的）。
    """
    tbl = {}
    if not os.path.exists(AUTOB):
        return tbl
    with open(AUTOB, encoding='utf-8') as f:
        doc = json.load(f)
    arr = doc.get('BuildPlans') if isinstance(doc, dict) else doc
    for b in arr:
        for path, ents in (b.get('Selections') or {}).items():
            for e in ents:
                g, grp = e.get('Selection'), e.get('FeatureGroup')
                if g and grp:
                    tbl.setdefault(g, grp)
    return tbl


def units_with_progression(doc):
    out = []

    def walk(o, bp):
        if isinstance(o, dict):
            b = o['Blueprint'] if isinstance(o.get('Blueprint'), str) else bp
            if 'CharacterLevel' in o and 'm_Selections' in o:
                out.append((b, o))
            for v in o.values():
                walk(v, b)
        elif isinstance(o, list):
            for v in o:
                walk(v, bp)

    walk(doc, None)
    return out


def from_save(cat, grptbl):
    with zipfile.ZipFile(SAVE) as z:
        doc = json.loads(z.read('party.json').decode('utf-8', errors='replace'))
    plans = []
    nogrp = 0
    for bp, pr in units_with_progression(doc):
        name = cat.get(bp, '')
        if name not in FROM_SAVE:
            continue
        pid, disp = FROM_SAVE[name]
        sel = collections.OrderedDict()
        grp = collections.OrderedDict()
        selbp = collections.OrderedDict()
        hw = oc = None
        order = []           # 保序记录职业链出现顺序
        for s in pr['m_Selections']:
            if s.get('Selection') == HOME_SEL:
                hw = s.get('Feature')
            if s.get('Selection') == OCC_SEL:
                oc = s.get('Feature')
            pathname = cat.get(s.get('Path'), '')
            if pathname in CHARGEN_PATHS:
                continue
            path, rank, feat = s.get('Path'), s.get('Level'), s.get('Feature')
            if not path or not feat:
                continue
            if path not in sel:
                sel[path] = collections.OrderedDict()
                grp[path] = collections.OrderedDict()
                selbp[path] = collections.OrderedDict()
                order.append(path)
            r = str(rank)
            sel[path].setdefault(r, [])
            grp[path].setdefault(r, [])
            selbp[path].setdefault(r, [])
            if feat not in sel[path][r]:
                sel[path][r].append(feat)
                g = grptbl.get(s.get('Selection'), '')
                if not g:
                    nogrp += 1
                grp[path][r].append(g)
                # Selection 蓝图 GUID —— 运行时可以直接读它的 FeatureGroup，
                # 比拿 RTAutoBuilder 当查表可靠（她那 36 条 Ascension 就全查不到）
                selbp[path][r].append(s.get('Selection') or '')
        # Ascension 一定是最后一段；T1/T2 按出现顺序
        chain = [p for p in order if cat.get(p, '') != 'AscensionCareerPath']
        plans.append({
            'id': pid, 'name': disp, 'source': 'save:' + name,
            'level': pr['CharacterLevel'],
            'homeworld': hw, 'origin': oc,
            'first': chain[0] if len(chain) > 0 else None,
            'second': chain[1] if len(chain) > 1 else None,
            'sel': sel, 'grp': grp, 'selbp': selbp,
        })
    if nogrp:
        print('  (%d 条没能补上 FeatureGroup —— 该 Selection 蓝图在 RTAutoBuilder 数据里没出现过)' % nogrp)
    return plans


def from_autobuilder(cat):
    if not os.path.exists(AUTOB):
        print('  (跳过 RTAutoBuilder: 文件不存在)')
        return []
    with open(AUTOB, encoding='utf-8') as f:
        doc = json.load(f)
    arr = doc.get('BuildPlans') if isinstance(doc, dict) else doc
    plans = []
    for prefix, pid, disp in FROM_AUTOB:
        src = None
        for b in arr:
            if (b.get('BuildComment') or '').startswith(prefix):
                src = b
                break
        if src is None:
            print('  !! RTAutoBuilder 里找不到「%s」' % prefix)
            continue
        sel = collections.OrderedDict()
        grp = collections.OrderedDict()
        for path, ents in (src.get('Selections') or {}).items():
            byrank = collections.OrderedDict(); bygrp = collections.OrderedDict()
            for e in ents:
                g = e.get('Selection')
                if not g:
                    continue
                r = str(e.get('Rank', 0))
                byrank.setdefault(r, []); bygrp.setdefault(r, [])
                if g not in byrank[r]:
                    byrank[r].append(g); bygrp[r].append(e.get('FeatureGroup') or '')
            sel[path] = byrank; grp[path] = bygrp
        plans.append({
            'id': pid, 'name': disp, 'source': 'rtautobuilder:' + prefix,
            'level': None,
            'homeworld': src.get('Homeworld'), 'origin': src.get('Origin'),
            'first': src.get('FirstArchetype'), 'second': src.get('SecondArchetype'),
            'sel': sel, 'grp': grp,
        })
    return plans


def main():
    dry = '--dry' in sys.argv
    cat = load_catalog()
    print('从 55 级存档抽取:')
    a = from_save(cat, selection_group_table())
    print('从 RTAutoBuilder 抽取:')
    b = from_autobuilder(cat)
    plans = a + b

    for p in plans:
        n = sum(len(v) for r in p['sel'].values() for v in r.values())
        segs = ' + '.join('%s(%d)' % (cat.get(k, k[:8]).replace('_CareerPath', ''),
                                      sum(len(v) for v in r.values()))
                          for k, r in p['sel'].items())
        print('  %-26s %-3s 条%-4d  %s' % (p['id'], p['level'] or '-', n, segs))

    doc = {
        '_说明': 'DynastyRetinue 自带的加点方案。开发期由 ref/rt_probe/mkplans.py 从作者的 55 级存档和 '
                 'RTAutoBuilder 方案离线抽取而来；运行时 mod 只读这个文件，不读存档、不依赖 RTAutoBuilder，'
                 '开新档即可用。sel = 职业链GUID -> rank -> [特性GUID]。全是原版蓝图，不新增 AssetId。',
        'plans': plans,
    }
    if dry:
        print('\n--dry: 未写文件')
        return
    for path in OUT:
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, 'w', encoding='utf-8') as f:
            json.dump(doc, f, ensure_ascii=False, indent=1)
        print('-> %s  (%.1f KB)' % (path, os.path.getsize(path) / 1024.0))


if __name__ == '__main__':
    main()
