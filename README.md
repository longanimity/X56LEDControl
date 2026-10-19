# X56Led

Sets the RGB backlight and brightness of a Logitech/Saitek X56 HOTAS stick (VID 0738, PID 2221) and throttle (PID A221).

```
dotnet build -c Release
bin\Release\net10.0-windows\X56Led.exe                     # GUI
X56Led list
X56Led get
X56Led set --rgb FF6000 --brightness 80                    # both units
X56Led set --stick --rgb 00A0FF --save                     # stick only, persist to EEPROM
```

## Protocol (recovered from Controllers\X56.dll and Sd.Branding.Flight.Rhino.dll)

All commands are HID feature reports sent to the collection with `FeatureReportByteLength != 0` (interface MI_02). Each report is zero-padded to that length.

| Packet                 | Meaning                                           |
|------------------------|---------------------------------------------------|
| `09 00 03 RR GG BB`    | set RGB colour                                    |
| `09 00 02 NN`          | set brightness, 0-100                             |
| `09 02 03` / `09 02 02`| query colour or brightness; the reply is input report `xx 09 01/02 ff v...` |
| `01 01`                | save current settings to EEPROM                   |

Each unit has a single RGB zone. Per-button colours are not exposed by the protocol the Logitech software uses.
