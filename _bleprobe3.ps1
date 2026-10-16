$ErrorActionPreference = 'Continue'
$out = 'D:\deepseek\bsod\ble-probe3.txt'
function W($s) { $s | Out-File $out -Append -Encoding UTF8 }
"start" | Out-File $out -Encoding UTF8
[void][Windows.Devices.Bluetooth.BluetoothAdapter,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Radios.Radio,Windows.Devices.Radios,ContentType=WindowsRuntime]
[void][Windows.Devices.Radios.RadioState,Windows.Devices.Radios,ContentType=WindowsRuntime]
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$m = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $t) { $task = $m.MakeGenericMethod($t).Invoke($null, @($op)); if (-not $task.Wait(20000)) { throw 'timeout' }; $task.Result }
# radios
try {
  $radios = Await ([Windows.Devices.Radios.Radio]::GetRadiosAsync()) ([System.Collections.Generic.IReadOnlyList[Windows.Devices.Radios.Radio]])
  foreach ($r in $radios) { W ('radio: ' + $r.Kind + '  state=' + $r.State + '  name=' + $r.Name) }
} catch { W ('radio err: ' + $_.Exception.Message) }
# service provider with explicit status dump
try {
  $g = New-Object Guid('0000ffe0-0000-1000-8000-00805f9b34fb')
  $res = Await ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider]::CreateAsync($g)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult])
  if ($res -eq $null) { W 'res NULL (async returned null)' } else {
    W ('res type = ' + $res.GetType().FullName)
    $st = $res.Status
    W ('status type = ' + $(if ($st -eq $null) { 'null' } else { $st.GetType().FullName }))
    W ('status value = [' + [string]$st + ']  int=' + $(try { [int]$st } catch { 'n/a' }))
    W ('errorMessage = ' + $res.ErrorMessage)
    W ('provider = ' + $(if ($res.Provider -eq $null) { 'NULL' } else { 'ok' }))
  }
} catch { W ('CreateAsync err: ' + $_.Exception.GetType().FullName + ' :: ' + $_.Exception.Message) }
# developer mode / sideloading
$k = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
W ('AllowDevelopmentWithoutDevLicense = ' + $k.AllowDevelopmentWithoutDevLicense)
W ('AllowAllTrustedApps = ' + $k.AllowAllTrustedApps)
# 当前进程是否有包标识
$id = [System.Diagnostics.Process]::GetCurrentProcess().Id
W ('GetCurrentPackageFullName test via kernel32 not done; exe path = ' + [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName)
W 'done'
