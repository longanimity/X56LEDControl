using System.Globalization;

namespace X56LedControl;

internal static class Program
{
    const string Usage = """
        X56Led - Logitech/Saitek X56 HOTAS LED control

          X56Led                                   open the GUI
          X56Led list                              show detected stick/throttle
          X56Led get                               read current colour + brightness
          X56Led set [--stick|--throttle] [--rgb RRGGBB] [--brightness 0-100] [--save]

        With no --stick/--throttle, 'set' applies to both. --save writes to EEPROM so the
        setting survives unplugging.
        """;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
            return 0;
        }

        Native.AttachConsole(-1);
        Console.WriteLine();
        try { return RunCli(args).GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine("error: " + ex.Message); return 1; }
    }

    static async Task<int> RunCli(string[] args)
    {
        var devices = X56Device.Enumerate();
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "list":
                    if (devices.Count == 0) Console.WriteLine("No X56 stick or throttle found.");
                    foreach (var d in devices)
                        Console.WriteLine($"{d.Name,-9} feature={d.FeatureLength}B input={d.InputLength}B  {d.Path}");
                    return devices.Count > 0 ? 0 : 2;

                case "get":
                    foreach (var d in devices)
                    {
                        var c = await d.QueryColourAsync();
                        var b = await d.QueryBrightnessAsync();
                        Console.WriteLine($"{d.Name,-9} colour={(c is { } v ? $"#{v.r:X2}{v.g:X2}{v.b:X2}" : "no reply")} brightness={(b?.ToString() ?? "no reply")}");
                    }
                    return devices.Count > 0 ? 0 : 2;

                case "set":
                    return Set(devices, args.Skip(1).ToArray());

                default:
                    Console.WriteLine(Usage);
                    return 1;
            }
        }
        finally { foreach (var d in devices) d.Dispose(); }
    }

    static int Set(List<X56Device> devices, string[] args)
    {
        bool stick = false, throttle = false, save = false;
        (byte r, byte g, byte b)? rgb = null;
        int? brightness = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--stick": stick = true; break;
                case "--throttle": throttle = true; break;
                case "--save": save = true; break;
                case "--rgb": rgb = ParseHex(args[++i]); break;
                case "--brightness": brightness = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"unknown option '{args[i]}'\n\n{Usage}");
            }
        }
        if (!stick && !throttle) stick = throttle = true;

        var targets = devices.Where(d => d.Kind == X56Kind.Stick ? stick : throttle).ToList();
        if (targets.Count == 0) { Console.WriteLine("No matching X56 device found."); return 2; }
        foreach (var d in targets)
        {
            if (rgb is { } c) d.SetColour(c.r, c.g, c.b);
            if (brightness is { } br) d.SetBrightness(br);
            if (save) d.SaveToEeprom();
            Console.WriteLine($"{d.Name}: ok");
        }
        return 0;
    }

    static (byte, byte, byte) ParseHex(string s)
    {
        s = s.TrimStart('#');
        if (s.Length != 6) throw new ArgumentException("--rgb expects RRGGBB");
        var v = Convert.FromHexString(s);
        return (v[0], v[1], v[2]);
    }
}
