import sys, io, json
sys.path.insert(0, r'D:\RT_RetinueMod\ref\rt_probe')
import bbp, _brainparse as bp

f, spans = bbp.load_pack()
names = bp.load_names()

ROWS = [
    ('adcf80a5d2a344d3814dcf3bb6b127f7', 'CURRENT RangedPositionalBaseBrain'),
    ('d97ca4c5eef04d0fbe4b2b495ef08f56', 'PICK    Eufrates_TechPriest_brain'),
    ('e03536a0e71a4095a351d1b6d66d7216', 'ALT     Inquisitor_Argenta_brain'),
    ('6a89947b47884b20babc0e922b54afaf', 'trap?   Ch04_CombatServitor_brain'),
    ('77f9ee8cb7354a48998eb45f3531c512', 'other   CombatServitorHeavyBrain'),
    ('a37c19ba455e4d12888a1d9be0c29978', 'other   BoardedShip_CombatServitor_Bolter'),
    ('a1355b2090af492cbcb64d98b10b042a', 'other   EufratesCSM_HeavyBolter_brain'),
    ('c3b4b0e56cec4a99b448bc7102971bd3', 'MINE    ArgentaAlternative_Brain'),
    ('0037a9bb9a4442ce8acd275d6fc50211', 'MINE    CultistLeaderSororitasBrain'),
    ('32111e7b22a54ea9842006987571569b', 'VANILLA DLC3_DL_Sororitas_HBolter_Brain'),
]
print('%-40s %-6s %-5s %-6s %-7s %-7s %-6s' %
      ('brain', 'UOL', 'prio', 'AoOesc', 'penalty', 'careful', 'tailOK'))
print('-' * 96)
for g, tag in ROWS:
    s, e = spans[g]
    f.seek(s); blob = f.read(e - s)
    d = bp.parse(blob)
    if d['partial']:
        print('%-40s  [HatedTargetConditions=%d -> unresolved]' % (tag, d['hated']))
        continue
    if not d.get('tail'):
        print('%-40s %-6s %-5d %-6s %-7s %-7s %-6s   AbilitySettings=%s'
              % (tag, d['use_only_listed'], len(d['priority']), '?', '?', '?',
                 'NO', d.get('n_ability_settings')))
        continue
    print('%-40s %-6s %-5d %-6s %-7.3g %-7s %-6s'
          % (tag, d['use_only_listed'], len(d['priority']),
             d['response_to_aoo_threat'], d['hit_unintended_penalty'],
             d['is_careful_shooter'], 'yes'))

print()
print('legend: AoOesc = ResponseToAoOThreat (escape melee threat BEFORE acting)')
print('        penalty = HitUnintendedTargetPenalty (ScatterShot ally veto weight)')
print()
print('ScatterShotTargetSelector.cs:143-160  score = sum(enemyHitChance)'
      ' - penalty*sum(allyHitChance);  shot rejected when score <= 0')
