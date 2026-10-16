$ErrorActionPreference = 'Continue'
$out = 'D:\deepseek\bsod\ble-cap2.txt'
function W($s) { $s | Out-File $out -Append -Encoding UTF8 }
"start" | Out-File $out -Encoding UTF8
try {
  [void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
  [void][Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderAdvertisingParameters,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
  [void][Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher,Windows.Devices.Bluetooth,ContentType=WindowsRuntime]
  Add-Type -AssemblyName System.Runtime.WindowsRuntime
  $asTask1 = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
  function Await($op, $t) { $m = $asTask1.MakeGenericMethod($t); $task = $m.Invoke($null, @($op)); if (-not $task.Wait(15000)) { throw 'timeout' }; $task.Result }
  $guid = [guid]'0000ffe0-0000-1000-8000-00805f9b34fb'
  $op = [Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider]::CreateAsync($guid)
  W ('op type = ' + $op.GetType().FullName)
  $res = Await $op ([Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProviderResult])
  if ($res -eq $null) { W 'result = NULL' } else {
    W ('result type = ' + $res.GetType().FullName)
    W ('Status = [' + [string]$res.Status + ']  (0=Success 1=RadioNotAvailable 2=RequestNotSupported)')
    $p = $res.Provider
    if ($p -eq $null) { W 'Provider = NULL' } else {
      W ('Provider ok, Service.Uuid = ' + $p.Service.Uuid)
      $adv = $p.Advertisement
      W ('adv type = ' + $adv.GetType().FullName)
      $adv.LocalName = 'WCS-BLE-TEST'
      $adv.IsDiscoverable = $true
      try { $p.StartAdvertising(); W 'StartAdvertising() ok' } catch { W ('StartAdvertising() err: ' + $_.Exception.Message) }
      Start-Sleep -Seconds 3
      try { $p.StopAdvertising(); W 'StopAdvertising() ok' } catch { W ('Stop: ' + $_.Exception.Message) }
    }
  }
} catch { W ('ERROR: ' + $_.Exception.GetType().FullName + ' :: ' + $_.Exception.Message) }
W '--- 再测纯广播（不含 GATT 服务）---'
try {
  $pub = New-Object Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher
  $pub.Advertisement.LocalName = 'WCS-ADV-TEST'
  $pub.Start(); W 'advertiser started (2s)'; Start-Sleep -Seconds 2; $pub.Stop(); W 'advertiser stopped'
} catch { W ('adv ERROR: ' + $_.Exception.Message) }
W 'done'
