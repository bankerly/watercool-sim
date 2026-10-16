using System;
using System.IO;
using System.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

public class BlePeripheralProbe
{
    const string LOG = @"D:\deepseek\bsod\ble-probe.txt";
    static void Log(string s)
    {
        Console.WriteLine(s);
        try { File.AppendAllText(LOG, s + "\r\n"); } catch { }
    }

    public static void Main()
    {
        try { File.Delete(LOG); } catch { }
        Log("=== BLE GATT Server probe ===");
        try
        {
            var adTask = BluetoothAdapter.GetDefaultAsync();
            var ad = adTask.AsTask().GetAwaiter().GetResult();
            if (ad == null) { Log("no bluetooth adapter"); return; }
            Log("adapter          = " + ad.DeviceId);
            Log("LE supported     = " + ad.IsLowEnergySupported);
            Log("central role     = " + ad.IsCentralRoleSupported);
            Log("PERIPHERAL role  = " + ad.IsPeripheralRoleSupported);
            Log("adv offload      = " + ad.IsAdvertisementOffloadSupported);

            Guid svc = new Guid("0000ffe0-0000-1000-8000-00805f9b34fb");
            Guid chr = new Guid("0000ffe1-0000-1000-8000-00805f9b34fb");
            Log("CreateAsync(service) ...");
            var res = GattServiceProvider.CreateAsync(svc).AsTask().GetAwaiter().GetResult();
            Log("  status = " + res.Status);
            var p = res.Provider;
            if (p == null) { Log("provider = null (likely needs package identity)"); return; }
            Log("  service uuid = " + p.Service.Uuid);

            var cp = new GattLocalCharacteristicParameters();
            cp.CharacteristicProperties = GattCharacteristicProperties.Read
                | GattCharacteristicProperties.Write
                | GattCharacteristicProperties.Notify;
            var cr = p.Service.CreateCharacteristicAsync(chr, cp).AsTask().GetAwaiter().GetResult();
            Log("  characteristic status = " + cr.Status);
            var ch = cr.Characteristic;
            if (ch != null)
            {
                ch.ReadRequested += (s, a) =>
                {
                    Log("[client READ]  offset=" + a.Offset);
                    try { a.GetDeferral().Complete(); } catch { }
                };
                ch.WriteRequested += (s, a) =>
                {
                    try
                    {
                        var r = a.GetRequestAsync().AsTask().GetAwaiter().GetResult();
                        var buf = new Windows.Storage.Streams.Buffer(r.Value.Length);
                        r.Value.ToBuffer(buf);   // best effort
                        Log("[client WRITE] len=" + r.Value.Length);
                    }
                    catch (Exception ex) { Log("[client WRITE] err " + ex.Message); }
                };
            }

            var adv = p.Advertisement;
            adv.LocalName = "WCS-BLE-TEST";
            adv.IsDiscoverable = true;
            try { p.StartAdvertising(); Log("advertising: ON (5 s)"); }
            catch (Exception ex) { Log("StartAdvertising failed: " + ex.Message); }
            Thread.Sleep(5000);
            try { p.StopAdvertising(); Log("advertising: OFF"); } catch { }
            Log("=== probe done ===");
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex.GetType().FullName + " :: " + ex.Message);
        }
    }
}
