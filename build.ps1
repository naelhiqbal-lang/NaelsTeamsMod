$ErrorActionPreference = 'Stop'

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$managed = 'C:\Program Files (x86)\Steam\steamapps\common\Super Battle Golf\Super Battle Golf_Data\Managed'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

& $csc /nologo /target:library /optimize+ /out:"$root\NaelsTeamsMod-0.4.17.dll" `
  /reference:"$managed\netstandard.dll" `
  /reference:"$root\core\BepInEx.dll" `
  /reference:"$root\core\0Harmony.dll" `
  /reference:"$managed\GameAssembly.dll" `
  /reference:"$managed\SharedAssembly.dll" `
  /reference:"$managed\Mirror.dll" `
  /reference:"$managed\UnityEngine.dll" `
  /reference:"$managed\UnityEngine.CoreModule.dll" `
  /reference:"$managed\Unity.InputSystem.dll" `
  /reference:"$managed\UnityEngine.InputLegacyModule.dll" `
  /reference:"$managed\UnityEngine.IMGUIModule.dll" `
  /reference:"$managed\UnityEngine.UI.dll" `
  /reference:"$managed\UnityEngine.UIModule.dll" `
  /reference:"$managed\UnityEngine.TextRenderingModule.dll" `
  /reference:"$managed\Unity.TextMeshPro.dll" `
  "$root\NaelsTeamsMod.cs"
