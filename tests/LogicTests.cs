using System; using AcerBatteryControl;
// Logic tests without hardware. Require ACERBAT_DRYRUN=1 and ACERBAT_DATADIR (temporary folder):
// otherwise refuse to start, because the dry-run check calls AcerWmi.SetMode.
static class T {
  [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SetErrorMode(uint mode);
  static int fails;
  static void Check(string name, bool ok) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name); if (!ok) fails++; }
  static int Main() {
    // no Windows error dialog; an exception on any thread prints the error and exits with 3
    SetErrorMode(0x0001 | 0x0002); // SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX
    AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) {
      Console.WriteLine("FAIL unhandled exception: " + e.ExceptionObject); Console.Out.Flush(); Environment.Exit(3);
    };
    if (Environment.GetEnvironmentVariable("ACERBAT_DRYRUN") != "1" || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ACERBAT_DATADIR"))) {
      Console.Error.WriteLine("Refused: set ACERBAT_DRYRUN=1 and ACERBAT_DATADIR before running tests."); return 2;
    }
    if (Environment.GetEnvironmentVariable("ACERBAT_TEST_CRASH") == "1") {
      // verify.ps1 self-test: must exit with 3, without opening dialogs or hanging
      var th = new System.Threading.Thread(delegate() { throw new InvalidOperationException("simulated crash"); });
      th.Start(); th.Join(); System.Threading.Thread.Sleep(10000); return 0;
    }
    Console.WriteLine("datadir: " + Paths.DataDir);
    var c = new Config(); c.LimitDesired=false; c.Calibrating=true; c.CalibStartedUtc=new DateTime(2026,11,17,9,30,0,DateTimeKind.Utc); c.SavedLidAction=1; c.SavedLidScheme=Guid.NewGuid().ToString(); c.Save();
    var d = Config.Load();
    Check("config roundtrip", d.LimitDesired==false && d.Calibrating && d.CalibStartedUtc==c.CalibStartedUtc && d.SavedLidAction==1 && d.SavedLidScheme==c.SavedLidScheme);
    var r = new IpcResult(); r.Ok=true;
    string body = "version=2.1.0\nfw_valid=1\nhealth_supported=1\ncalib_supported=1\nhealth_on=1\ncalib_on=0\nfw_raw=list=0x03 status=01-00-00-00-00 ret=00-00\nlimit_desired=on\ncalibrating=0\ncalib_started=\npercent=79\non_ac=1\ncharging=0\ndischarging=0\nrate_mw=0\nvoltage_mv=16420\ncurrent_ma=-12\ntemp_c=31.4\nremaining_mwh=38000\nfull_mwh=48100\ndesign_mwh=57480\ncycles=212\nmodel=SANYO AP18E8M\ncorrections_24h=2\nlast_correction=2026-11-17T10:02:00.0000000+01:00\nlast_error=\nevent_seq=3\nevent_text=x\nconflicts=Acer Quick Access Service\n";
    foreach (string line in body.Split('\n')) { int eq=line.IndexOf('='); if(eq>0) r.Data[line.Substring(0,eq)]=line.Substring(eq+1); }
    string[] v = StatusView.Values(r);
    for (int i=0;i<v.Length;i++) Console.WriteLine("  "+StatusView.Labels[i].PadRight(30)+v[i]);
    Check("labels/values aligned", v.Length == StatusView.Labels.Length);
    Console.WriteLine("state: " + StatusView.State(r));
    r.Data["health_on"]="0"; Console.WriteLine("mismatch: " + StatusView.LimitLine(r));
    r.Data["calibrating"]="1"; r.Data["calib_started"]="2026-11-17T09:30:00.0000000+01:00"; Console.WriteLine("calib: " + StatusView.Values(r)[9] + " | " + StatusView.LimitLine(r) + " | " + StatusView.State(r));
    Log.Write("test line"); Check("log tail", Log.Tail(1).EndsWith("test line"));
    bool blocked = false;
    try { AcerWmi.SetMode(AcerWmi.ModeHealth, true); } catch (AcerException ex) { blocked = ex.Message.StartsWith("[dry-run]"); }
    Check("dry-run blocks SetBatteryHealthControl", blocked);
    blocked = false;
    try { Power.WriteAcLid(Guid.Empty, 0); } catch (AcerException ex) { blocked = ex.Message.StartsWith("[dry-run]"); }
    Check("dry-run blocks lid action", blocked);
    blocked = false;
    try { Installer.Install(); } catch (AcerException ex) { blocked = ex.Message.StartsWith("[dry-run]"); }
    Check("dry-run blocks installation", blocked);
    fails += ReviewTests.Run();
    Console.WriteLine(fails == 0 ? "RESULT: all tests passed" : "RESULT: " + fails + " tests failed");
    return fails == 0 ? 0 : 1;
  }
}
