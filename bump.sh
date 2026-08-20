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
#   DynastyRetinue.dll   本体
#   archetypes.json  Archetypes.cs 从 ModEntry.Path 读 —— 缺了只剩内置 4 分型，
#                    无精英、无装备表、无人名池，mod 名存实亡
#   plans.json       BuildPlans.cs 从同目录读 —— 缺了天赋全程回退"第一个可选项"
# 两条错路都要堵死：
#   · 只打 DLL+Info    ⇒ 玩家装上后"点了没反应"
#   · 直接打包部署目录 ⇒ 会把**开发者自己的 Settings.xml**（全部解除限制 + 开发区展开）
#     和含本机绝对路径的 dynasty_log.txt、上兆的 *.tsv 一起发出去
# 所以源一律取 src/ 与 bin/Release/，绝不从部署目录取。
#
# ── 3) DLL 没编译时必须报错 ──
# 原来写的是 `[ -f ... ] && cp ...`。set -e **不会**中断 AND-list 的失败条件，
# 于是 DLL 不存在时静默跳过，而 Info.json 已经涨到新版 —— 版本号和二进制对不上，
# 且毫无提示。改成显式判断 + exit 1。
set -e

[ -n "$1" ] || { echo "用法: sh bump.sh <版本号> [pack]"; exit 1; }
VER="$1"
R=src/DynastyRetinue
# ★译表完整性闸门★ 原来这里只有 [ -f l10n_en.json ]（查文件在不在），
# 于是「新加一句 L.T(...) 忘了补译文」会一路绿灯发出去，
# 英文玩家界面上就多一句中文，且没有任何报错。
py tools/check_l10n.py || { echo "x 本地化校验未通过，已中止。"; exit 1; }
# 名字用字门禁：游戏中文字体是子集，生僻字在名条上会渲染成方框，
# 而 mod 面板用 Unity 默认字体显示正常 —— 对着面板永远查不出来。
py tools/check_names.py || { echo "x 名字用字校验未通过，已中止。"; exit 1; }
BIN=$R/bin/Release
# 部署目录。默认按 Windows 上 UMM 给这个游戏的标准位置推导（$HOME 就是 C:\Users\你）。
# 装在别处的话，跑之前设一下环境变量即可：
#   DR_DEPLOY="/e/Games/.../UnityModManager/DynastyRetinue" sh bump.sh 1.0.0
D="${DR_DEPLOY:-$HOME/AppData/LocalLow/Owlcat Games/Warhammer 40000 Rogue Trader/UnityModManager/DynastyRetinue}"

# ★先生成数据文件指纹，再编译★ 顺序不能反 —— BuildManifest.cs 要参与编译。
# 指纹只用于诊断包里标注"这份配表被改过没有"，不做任何拦截，正常玩家无感。
echo "生成数据指纹……"
py tools/gen_manifest.py "$VER"

# ★先编译，且编译失败就停★
# 只查 DLL 存在是不够的：编译失败时上一次的 DLL 还在，于是 Info.json 涨到新版、
# 二进制却是旧的，还照样打成发布包 —— v0.55.0 就这么发出去过一次（4 个编译错误被无视）。
echo "编译中……"
( cd "$R" && dotnet build -c Release -v:m ) > /tmp/dynasty_build.log 2>&1 || {
  echo "✗ 编译失败，已中止。错误："; grep -E "error" /tmp/dynasty_build.log | sort -u | head -10; exit 1; }
if grep -qE ": error " /tmp/dynasty_build.log; then
  echo "✗ 编译有错误，已中止："; grep -E ": error " /tmp/dynasty_build.log | sort -u | head -10; exit 1
fi
[ -f "$BIN/DynastyRetinue.dll" ] || { echo "x $BIN/DynastyRetinue.dll 不存在"; exit 1; }
[ -f "$R/archetypes.json" ]  || { echo "x $R/archetypes.json 不存在"; exit 1; }
[ -f "$R/plans.json" ]       || { echo "x $R/plans.json 不存在"; exit 1; }
# 译文表现在是交付物的一部分。缺了不影响功能（会回落中文），但英文玩家会看到满屏中文，
# 而这个失败是静默的 —— 所以在这里显式挡一道，别让它悄悄漏发。
[ -f "$R/l10n_en.json" ]     || { echo "x $R/l10n_en.json 不存在（英文玩家会看到中文界面）"; exit 1; }

py -c "
import io,re
p=r'$R/Info.json'; s=io.open(p,encoding='utf-8-sig').read()
s=re.sub(r'\"Version\"\s*:\s*\"[^\"]*\"','\"Version\": \"$VER\"',s)
io.open(p,'w',encoding='utf-8-sig',newline='\n').write(s)
"

# ★部署目录不存在就先建★ 干净环境（新克隆、或把部署目录改名做干净安装测试）下
# 原来会直接死在 `cp: No such file or directory`，而且是在编译成功之后才死，
# 看起来像编译出了问题。mkdir -p 是幂等的，加着无害。
mkdir -p "$D"

cp "$R/Info.json"        "$D/Info.json"
cp "$BIN/DynastyRetinue.dll" "$D/"
if [ -f "$BIN/DynastyRetinue.pdb" ]; then cp "$BIN/DynastyRetinue.pdb" "$D/"; fi
# ★数据文件也要拷★ 原来只拷 Info+DLL，于是改了 src/ 的 archetypes.json / plans.json /
# l10n_en.json 之后跑 bump.sh，部署目录里还是旧的 —— 游戏读的是部署目录，
# 所以现象是"改了没生效"，而且不报错。l10n_en.json 更隐蔽：缺了会静默回落中文，
# 看起来就只是"英文没做好"。
cp "$R/archetypes.json"  "$D/"
cp "$R/plans.json"       "$D/"
if [ -f "$R/l10n_en.json" ]; then cp "$R/l10n_en.json" "$D/"; fi
echo "已部署 v$VER 到本机"

if [ "$2" = "pack" ]; then
  OUT=dist/DynastyRetinue
  rm -rf dist && mkdir -p "$OUT"
  cp "$R/Info.json" "$R/archetypes.json" "$R/plans.json" "$OUT/"
  # 译文表：缺了只是界面不显示英文，不影响功能；但既然有就该发
  if [ -f "$R/l10n_en.json" ]; then cp "$R/l10n_en.json" "$OUT/"; fi
  cp "$BIN/DynastyRetinue.dll" "$OUT/"
  if [ -f README.md ]; then cp README.md "$OUT/"; fi
  # 许可证必须随包发 —— MIT 要求"保留本许可文件"，不发就等于自己没遵守自己的条款
  [ -f LICENSE ] || { echo "x LICENSE 不存在"; exit 1; }
  cp LICENSE "$OUT/"
  # 不打 pdb（玩家用不上，只让包变大）；不打 Settings.xml（那是本机配置）；
  # 不打 *.tsv / dynasty_log.txt（调试数据，且日志含本机绝对路径）
  py -c "
import shutil; shutil.make_archive(r'dist/DynastyRetinue-$VER','zip','dist','DynastyRetinue')"
  echo "发布包: dist/DynastyRetinue-$VER.zip"
  ls -l dist/
fi

grep '"Version"' "$D/Info.json"
