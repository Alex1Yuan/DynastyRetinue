import sys, io, json
sys.path.insert(0, r'D:\RT_RetinueMod\ref\rt_probe')
import bbp, _brainparse as bp

f, spans = bbp.load_pack()
names = bp.load_names()
rows = [l.rstrip('\n').split('\t') for l in
        io.open(r'D:\RT_RetinueMod\ref\rt_probe\brains.tsv', encoding='utf-8')
        if l.strip()]

# ---- global self-check: does the tail parse land exactly on <name><guid>?
good = bad = skip = 0
mismatch = []
for name, g in rows:
    s, e = spans[g]
    f.seek(s); blob = f.read(e - s)
    try:
        d = bp.parse(blob)
    except Exception:
        skip += 1; continue
    if d.get('partial') or d.get('protopatch') or d.get('n_ability_settings'):
        skip += 1; continue
    if d.get('tail') and d.get('tail_name') == name and d.get('tail_guid') == g:
        good += 1
    else:
        bad += 1
        mismatch.append((name, g, d.get('tail_name'), d.get('tail_guid')))
print('FULL-RECORD SELF-CHECK: %d records parsed end-to-end and landed exactly on '
      'their own name+guid; %d mismatched; %d skipped (AbilitySettings/Hated present)'
      % (good, bad, skip))
for m in mismatch[:8]:
    print('   MISMATCH', m)

print()
CAND = [
    ('adcf80a5d2a344d3814dcf3bb6b127f7', 'CURRENT  RangedPositionalBaseBrain'),
    ('e03536a0e71a4095a351d1b6d66d7216', 'MINE     Inquisitor_Argenta_brain'),
    ('c3b4b0e56cec4a99b448bc7102971bd3', 'MINE     ArgentaAlternative_Brain'),
    ('0037a9bb9a4442ce8acd275d6fc50211', 'MINE     CultistLeaderSororitasBrain'),
    ('d97ca4c5eef04d0fbe4b2b495ef08f56', 'FOUND    Eufrates_TechPriest_brain'),
    ('6a89947b47884b20babc0e922b54afaf', 'FOUND    Ch04_CombatServitor_brain'),
    ('1a538fa5f9094c3f988baf6393a67bb0', 'FOUND    TreasureWorld_LemanRussTank_Brain'),
    ('32111e7b22a54ea9842006987571569b', 'VANILLA  DLC3_DL_Sororitas_HBolter_Brain'),
    ('a1355b2090af492cbcb64d98b10b042a', 'OTHER    EufratesCSM_HeavyBolter_brain'),
]
for g, tag in CAND:
    s, e = spans[g]
    f.seek(s); blob = f.read(e - s)
    d = bp.parse(blob)
    a = [t for t, fa in d['score_order'] if fa != 'Ignored']
    print('-' * 96)
    print(tag, ' ', g)
    print('   active ScoreOrder : ' + ' > '.join(a))
    print('   ignored           : ' + (', '.join(t for t, fa in d['score_order']
                                                 if fa == 'Ignored') or '(none)'))
    if d['partial']:
        print('   [HatedTargetConditions=%d -> rest unresolved]' % d['hated']); continue
    print('   UseOnlyListed=%-5s  prio=%d  mov=%d  AbilitySettings=%s'
          % (d['use_only_listed'], len(d['priority']), len(d['movement']),
             d.get('n_ability_settings')))
    if d.get('tail'):
        print('   IsCarefulShooter=%-5s IgnoreAoOForCasting=%-5s'
              % (d['is_careful_shooter'], d['ignore_aoo_for_casting']))
        print('   ResponseToAoOThreat=%-5s (before)   ResponseToAoOThreatAfterAbilities=%-5s'
              % (d['response_to_aoo_threat'], d['response_to_aoo_threat_after']))
        print('   MeleeBrainType=%d  HitUnintendedTargetPenalty=%.3g  '
              'BeforeMove=%d MoveAndCast=%d AfterMove=%d'
              % (d['melee_brain_type'], d['hit_unintended_penalty'],
                 d['n_before_move'], d['n_move_and_cast'], d['n_after_move']))
        print('   [tail self-check OK: landed on %r]' % d['tail_name'])
    else:
        print('   [tail not decodable: AbilitySettings=%s err=%s]'
              % (d.get('n_ability_settings'), d.get('tail_err')))
