using System;

namespace AcerBatteryControl
{
    // The engine does not depend on the WMI provider: tests use only a simulated device.
    interface IBatteryHardware
    {
        HealthStatus GetStatus();
        int SetMode(byte mask, bool on);
        Telemetry ReadStandard();
        void ReadFirmwareSensors(Telemetry telemetry);
    }

    sealed class WmiHardware : IBatteryHardware
    {
        public HealthStatus GetStatus() { return AcerWmi.GetStatus(); }
        public int SetMode(byte mask, bool on) { return AcerWmi.SetMode(mask, on); }
        public Telemetry ReadStandard() { return AcerWmi.ReadStandard(); }
        public void ReadFirmwareSensors(Telemetry telemetry) { AcerWmi.ReadFirmwareSensors(telemetry); }
    }
}
