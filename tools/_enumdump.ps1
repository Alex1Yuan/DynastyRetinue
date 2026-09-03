# 通用枚举 dumper。用法: _enumdump.ps1 -TypeName <FullName> [-Asm Code.dll]
param([Parameter(Mandatory=$true)][string]$TypeName, [string]$Asm = "Code.dll")
$managed = "H:\SteamLibrary\steamapps\common\Warhammer 40,000 Rogue Trader\WH40KRT_Data\Managed"
Add-Type -Path (Join-Path $managed "..\..\..\..\..\..\nonexist.dll") -EA SilentlyContinue
$asmPath = Join-Path $managed $Asm
[System.AppDomain]::CurrentDomain.add_AssemblyResolve({
  param($s,$e)
  $n = ($e.Name -split ',')[0] + ".dll"
  $p = Join-Path $managed $n
  if (Test-Path $p) { return [System.Reflection.Assembly]::LoadFrom($p) }
  return $null
})
$a = [System.Reflection.Assembly]::LoadFrom($asmPath)
$t = $a.GetType($TypeName, $false)
if ($null -eq $t) { Write-Output "NOT FOUND: $TypeName in $Asm"; exit 1 }
Write-Output ("TYPE " + $t.FullName + "  isEnum=" + $t.IsEnum + "  underlying=" + $(if($t.IsEnum){[Enum]::GetUnderlyingType($t).Name}else{"-"}))
if ($t.IsEnum) {
  foreach ($n in [Enum]::GetNames($t)) {
    $v = [Convert]::ChangeType([Enum]::Parse($t,$n), [Enum]::GetUnderlyingType($t))
    Write-Output ("  " + $n + " = " + $v)
  }
}
