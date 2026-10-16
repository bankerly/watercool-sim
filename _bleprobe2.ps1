$ErrorActionPreference = 'Continue'
$out = 'D:\deepseek\bsod\ble-probe2.txt'
function W($s) { $s | Out-File $out -Append -Encoding UTF8 }
"start" | Out-File $out -Encoding UTF8
# preload every WinRT type used as a generic argument
[void][Windows.Devices.Bluetooth.BluetoothAdapter,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicResult,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicParameters,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
[void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$m = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, $t) { $task = $m.MakeGenericMethod($t).Invoke($null, @($op)); if (-not $task.Wait(15000)) { throw 'timeout' }; $task.Result }
try {
  $ad = Await ([Windows.Devices.Bluetooth.BluetoothAdapter]::GetDefaultAsync()) ([Windows.Devices.Bluetooth.BluetoothAdapter])
  W ('central=' + $ad.IsCentralRoleSupported + ' peripheral=' + $ad.IsPeripheralRoleSupported)
  $svcGuid = New-Object Guid('0000ffe0-0000-1000-8000-00805f9b34fb')
  $chrGuid = New-Object Guid('0000ffe1-0000-1000-8000-00805f9b34fb')
  $res = Await ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider]::CreateAsync($svcGuid)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult])
  W ('CreateAsync status = ' + [string]$res.Status)
  $p = $res.Provider
  if ($p -eq $null) { W 'provider NULL' } else {
    W ('service uuid = ' + $p.Service.Uuid)
    $cp = New-Object Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicParameters
    $cp.CharacteristicProperties = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties]::Read -bor [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties]::Write -bor [Windows.Devices.Bluetooth.GenericAttributeProfile.GattCharacteristicProperties]::Notify
    $cr = Await ($p.Service.CreateCharacteristicAsync($chrGuid, $cp)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattLocalCharacteristicResult])
    W ('CreateCharacteristic status = ' + [string]$cr.Status)
    $adv = $p.Advertisement
    $adv.LocalName = 'WCS-BLE-TEST'
    $adv.IsDiscoverable = $true
    try { $p.StartAdvertising(); W 'advertising ON (6s)' } catch { W ('StartAdvertising err: ' + $_.Exception.Message) }
    Start-Sleep -Seconds 6
    try { $p.StopAdvertising(); W 'advertising OFF' } catch { }
  }
} catch { W ('ERROR: ' + $_.Exception.GetType().FullName + ' :: ' + $_.Exception.Message) }
W 'done'
