#!/bin/sh
# 版本号唯一入口 + 发布包打包。用法: sh bump.sh 0.48.0 [pack]
#
# ── 1) 版本号 ──
# 原来用 sed '"Version": "旧号"' -> '"新号"' 就地替换，匹配串写的是**上一个版本号**。
# 仓库那份漂了一次之后，此后每次替换都静默无匹配 —— 部署目录涨到 0.44.0，
# 仓库停在 0.20.0，二十多个版本没人发现。发布物以仓库为准，所以那是真问题。
# 现在用正则替换，不依赖旧值。
#
# ── 2) 发布包必需四件，不是两件 ──
#   Info.json        UMM 读
#   KgdRetinue.dll   本体
#   archetypes.json  Archetypes.cs 从 ModEntry.Path 读 —— 缺了只剩内置 4 分型，
#                    无精英、无装备表、无人名池，mod 名存实亡
#   plans.json       BuildPlans.cs 从同目录读 —— 缺了天赋全程回退"第一个可选项"
# 两条错路都要堵死：
#   · 只打 DLL+Info    ⇒ 玩家装上后"点了没反应"
#   · 直接打包部署目录 ⇒ 会把**开发者自己的 Settings.xml**（全部解除限制 + 开发区展开）
#     和含本机绝对路径的 kgd_log.txt、上兆的 *.tsv 一起发出去
# 所以源一律取 src/ 与 bin/Release/，绝不从部署目录取。
#
# ── 3) DLL 没编译时必须报错 ──
# 原来写的是 `[ -f ... ] && cp ...`。set -e **不会**中断 AND-list 的失败条件，
# 于是 DLL 不存在时静默跳过，而 Info.json 已经涨到新版 —— 版本号和二进制对不上，
# 且毫无提示。改成显式判断 + exit 1。
set -e

[ -n "$1" ] || { echo "用法: sh bump.sh <版本号> [pack]"; exit 1; }
VER="$1"
R=src/KgdRetinue
BIN=$R/bin/Release
D="C:/Users/kyua805/AppData/LocalLow/Owlcat Games/Warhammer 40000 Rogue Trader/UnityModManager/KgdRetinue"

# ★先编译，且编译失败就停★
# 只查 DLL 存在是不够的：编译失败时上一次的 DLL 还在，于是 Info.json 涨到新版、
# 二进制却是旧的，还照样打成发布包 —— v0.55.0 就这么发出去过一次（4 个编译错误被无视）。
echo "编译中……"
( cd "$R" && dotnet build -c Release -v:m ) > /tmp/kgd_build.log 2>&1 || {
  echo "✗ 编译失败，已中止。错误："; grep -E "error" /tmp/kgd_build.log | sort -u | head -10; exit 1; }
if grep -qE ": error " /tmp/kgd_build.log; then
  echo "✗ 编译有错误，已中止："; grep -E ": error " /tmp/kgd_build.log | sort -u | head -10; exit 1
fi
[ -f "$BIN/KgdRetinue.dll" ] || { echo "x $BIN/KgdRetinue.dll 不存在"; exit 1; }
[ -f "$R/archetypes.json" ]  || { echo "x $R/archetypes.json 不存在"; exit 1; }
[ -f "$R/plans.json" ]       || { echo "x $R/plans.json 不存在"; exit 1; }

py -c "
import io,re
p=r'$R/Info.json'; s=io.open(p,encoding='utf-8-sig').read()
s=re.sub(r'\"Version\"\s*:\s*\"[^\"]*\"','\"Version\": \"$VER\"',s)
io.open(p,'w',encoding='utf-8-sig',newline='\n').write(s)
"

cp "$R/Info.json"        "$D/Info.json"
cp "$BIN/KgdRetinue.dll" "$D/"
if [ -f "$BIN/KgdRetinue.pdb" ]; then cp "$BIN/KgdRetinue.pdb" "$D/"; fi
echo "已部署 v$VER 到本机"

if [ "$2" = "pack" ]; then
  OUT=dist/KgdRetinue
  rm -rf dist && mkdir -p "$OUT"
  cp "$R/Info.json" "$R/archetypes.json" "$R/plans.json" "$OUT/"
  cp "$BIN/KgdRetinue.dll" "$OUT/"
  if [ -f README.md ]; then cp README.md "$OUT/"; fi
  # 不打 pdb（玩家用不上，只让包变大）；不打 Settings.xml（那是本机配置）；
  # 不打 *.tsv / kgd_log.txt（调试数据，且日志含本机绝对路径）
  py -c "
import shutil; shutil.make_archive(r'dist/KgdRetinue-$VER','zip','dist','KgdRetinue')"
  echo "发布包: dist/KgdRetinue-$VER.zip"
  ls -l dist/
fi

grep '"Version"' "$D/Info.json"
