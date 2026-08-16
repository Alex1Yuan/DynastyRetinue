#!/bin/sh
# 版本号唯一入口。用法: sh bump.sh 0.46.0
#
# 起因：原来每次发版都用 sed '"Version": "旧号"' -> '"新号"' 就地替换，匹配串写的是
# **上一个版本号**。仓库那份漂了一次之后，此后每次替换都静默无匹配 ——
# 部署目录一路涨到 0.44.0，仓库停在 0.20.0，二十多个版本没人发现。
# 发布物以仓库为准，所以这是真问题。现在用正则替换，不依赖旧值。
set -e
[ -n "$1" ] || { echo "用法: sh bump.sh <版本号>"; exit 1; }
R=src/KgdRetinue
D="C:/Users/kyua805/AppData/LocalLow/Owlcat Games/Warhammer 40000 Rogue Trader/UnityModManager/KgdRetinue"
py -c "
import io,re,sys
p=r'$R/Info.json'; s=io.open(p,encoding='utf-8-sig').read()
s=re.sub(r'\"Version\"\s*:\s*\"[^\"]*\"','\"Version\": \"$1\"',s)
io.open(p,'w',encoding='utf-8-sig',newline='\n').write(s)
"
cp "$R/Info.json" "$D/Info.json"
[ -f "$R/bin/Release/KgdRetinue.dll" ] && cp "$R/bin/Release/KgdRetinue.dll" "$D/"
[ -f "$R/bin/Release/KgdRetinue.pdb" ] && cp "$R/bin/Release/KgdRetinue.pdb" "$D/"
grep '"Version"' "$D/Info.json"
