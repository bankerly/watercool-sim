$ErrorActionPreference = 'Continue'
$out = 'D:\deepseek\bsod\ble-cap.txt'
function W($s) { $s | Out-File $out -Append -Encoding UTF8 }
"start" | Out-File $out -Encoding UTF8
try {
  [void][Windows.Devices.Bluetooth.BluetoothAdapter,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
  [void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
  [void][Windows.Devices.Radios.Radio,Windows.Devices.Radios,ContentType=WindowsRuntime]
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $asTask1 = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  function Await($op, $t) { $m = $asTask1.MakeGenericMethod($t); $task = $m.Invoke($null, @($op)); if (-not $task.Wait(15000)) { throw 'timeout' }; $task.Result }
  $adapter = Await ([Windows.Devices.Bluetooth.BluetoothAdapter]::GetDefaultAsync()) ([Windows.Devices.Bluetooth.BluetoothAdapter])
  if ($adapter -eq $null) { W 'NO ADAPTER' } else {
    W ('adapter=' + $adapter.DeviceId)
    W ('IsLowEnergySupported=' + $adapter.IsLowEnergySupported)
    W ('IsCentralRoleSupported=' + $adapter.IsCentralRoleSupported)
    W ('IsPeripheralRoleSupported=' + $adapter.IsPeripheralRoleSupported)
    W ('IsAdvertisementOffloadSupported=' + $adapter.IsAdvertisementOffloadSupported)
  }
  $guid = [guid]'0000ffe0-0000-1000-8000-00805f9b34fb'
  W ('CreateAsync GattServiceProvider ...')
  $res = Await ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider]::CreateAsync($guid)) ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult])
  W ('create status=' + $res.Status)
  $p = $res.Provider
  if ($p -ne $null) {
    $adv = $p.Advertisement
    $adv.LocalName = 'WCS-BLE-TEST'
    $adv.IsDiscoverable = $true
    $p.StartAdvertising()
    W 'advertising started (2s)'
    Start-Sleep -Seconds 2
    $p.StopAdvertising()
    W 'advertising stopped'
    $p = $null
  }
} catch { W ('ERROR: ' + $_.Exception.Message) }
W 'done'
