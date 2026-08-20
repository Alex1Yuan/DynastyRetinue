import json,io
d=json.load(io.open('archetypes.json',encoding='utf-8'))
for a in d['archetypes']:
    print('=== id=%s name=%s brain=%s'%(a.get('id'),a.get('name'),a.get('brain')))
    for k in ('unit','grantFeatures'):
        print('   %s = %s'%(k,a.get(k)))
    for k in ('gearT1','gearT2','gearT3'):
        v=a.get(k) or []
        print('   %s (%d): %s'%(k,len(v),v))
