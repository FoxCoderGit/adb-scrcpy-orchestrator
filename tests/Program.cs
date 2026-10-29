using AdbScrcpy;

static void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
}
var states = Orchestrator.ParseDevices("* daemon started successfully *\nList of devices attached\r\n" +
    "USB123\tdevice product:test\r\n192.168.1.10:5555 offline\nUSB456 unauthorized\n");
Check(states.Count == 3 && states["USB123"] == "device", "Parse online USB device");
Check(states["USB456"] == "unauthorized", "Preserve authorization state");
var config = new Config { Devices = [new("Demo A", "USB123")], OriginX = -1280, OriginY = 25 };
config.Validate();
var argsList = Layout.Arguments(config.Devices[0], 3, config);
Check(argsList[Array.IndexOf(argsList, "--window-x") + 1] == "-640", "Grid column with negative origin");
Check(argsList[Array.IndexOf(argsList, "--window-y") + 1] == "505", "Grid second row");
config.Devices.Add(new("Duplicate", "USB123"));
try { config.Validate(); throw new Exception("Accepted duplicate serial"); }
catch (ArgumentException) { }
Console.WriteLine("PASS: ADB states, grid geometry, duplicate configuration");
