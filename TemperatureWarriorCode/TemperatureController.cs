// Meadow
using Meadow;
using Meadow.Foundation.Sensors.Temperature;
using Meadow.Devices;
using Meadow.Hardware;
using Meadow.Units;
// Meadow
using Meadow;
using Meadow.Foundation.Sensors.Temperature;
using Meadow.Devices;
using Meadow.Hardware;
using Meadow.Units;
using System;
using System.Collections.Generic;
using System.Linq;

namespace TemperatureWarriorCode
{

    class TemperatureController
    {

        enum ControlState
        {
            Off,
            Heating,
            Cooling
        }

        ControlState state = ControlState.Off;


        bool isWorking = false;
        double outputUpperbound;
        double outputLowerbound;
        long sampleTimeInMilliseconds;
        double upperBound;
        double lowerBound;
        double setpoint;

        // Estado PID para control ON/OFF con inercia
        // Kp: Reacción, Ki: Corrección mínima, Kd: Freno fuerte
        double kp = 18.0;
        double ki = 0.05;
        double kd = 50.0;

        double integral = 0.0;
        double lastError = 0.0;
        double lastTimeSeconds = 0.0;

        public TemperatureController(double outputUpperbound,
            double outputLowerbound, long sampleTimeInMilliseconds)
        {

            this.outputUpperbound = outputUpperbound;
            this.outputLowerbound = outputLowerbound;
            this.sampleTimeInMilliseconds = sampleTimeInMilliseconds;
        }



        void SetWorkingMode(bool workingMode)
        {

            isWorking = workingMode;
        }

        public void setBounds(double upperBound, double lowerBound)
        {
            this.upperBound = upperBound;
            this.lowerBound = lowerBound;
        }

        public void Start()
        {
            integral = 0.0;
            lastError = 0.0;
            lastTimeSeconds = 0.0;
            SetWorkingMode(true);
        }

        public void Stop()
        {
            SetWorkingMode(false);
        }

        public void SetSetpoint(double setpoint)
        {
            if (Math.Abs(this.setpoint - setpoint) > 0.01)
            {
                integral = 0.0;
                lastError = 0.0;
                lastTimeSeconds = 0.0;
            }
            this.setpoint = setpoint;
        }

        // Llamada desde MeadowApp con la temperatura actual
        public int Update(double currentTemperatureCelsius, List<double> temperatureHistory, List<double> timeHistory)
{
    if (!isWorking) return 0;

    double dt = sampleTimeInMilliseconds / 1000.0;
    if (timeHistory.Count > 1)
    {
        double dtCandidate = timeHistory[^1] - timeHistory[^2];
        if (dtCandidate > 0.0)
            dt = dtCandidate;
    }

    // ===== PID =====
    double error = setpoint - currentTemperatureCelsius;

    integral += error * dt;
    double integralLimit = ki > 0.0 ? Math.Abs(outputUpperbound / ki) : 0.0;
    if (integralLimit > 0.0)
        integral = Clamp(integral, -integralLimit, integralLimit);

    double derivative = (error - lastError) / dt;

    double output = kp * error + ki * integral + kd * derivative;
    output = Clamp(output, outputLowerbound, outputUpperbound);

    lastError = error;

    // ===== CONTROL CON HISTÉRESIS REAL =====
    double enterBand = 1.2;   // °C
    double exitBand  = 0.4;   // °C

    switch (state)
    {
        case ControlState.Off:
            if (error > enterBand)
                state = ControlState.Heating;
            else if (error < -enterBand)
                state = ControlState.Cooling;
            break;

        case ControlState.Heating:
            if (error < exitBand)
                state = ControlState.Off;
            break;

        case ControlState.Cooling:
            if (error > -exitBand)
                state = ControlState.Off;
            break;
    }

    return state switch
    {
        ControlState.Heating => 1,
        ControlState.Cooling => 2,
        _ => 0
    };
}

        double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
