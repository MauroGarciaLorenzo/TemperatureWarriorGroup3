using System;
using System.Collections.Generic;

namespace TemperatureWarriorCode
{
    class TemperatureController
    {
        public enum ActuatorAction
        {
            Off = 0,
            Heat = 1,
            Cool = 2,
        }

        public readonly struct ControlSignal
        {
            public ControlSignal(ActuatorAction action, double duty, double output, double setpoint, double error)
            {
                Action = action;
                Duty = duty;
                Output = output;
                Setpoint = setpoint;
                Error = error;
            }

            public ActuatorAction Action { get; }
            public double Duty { get; }
            public double Output { get; }
            public double Setpoint { get; }
            public double Error { get; }
        }

        bool isWorking = false;
        double outputUpperbound;
        double outputLowerbound;
        long sampleTimeInMilliseconds;
        double upperBound;
        double lowerBound;
        double setpoint;

        double kp = 0.6;
        double ki = 0.1;
        double kd = 0.125;

        double integral = 0.0;
        double lastError = 0.0;
        double lastTimeSeconds = 0.0;

        List<double> targetTimeSeconds = new List<double>();
        List<double> targetTemp = new List<double>();
        bool hasTargetCurve = false;
        double curveStartTimeSeconds = double.NaN;

        public TemperatureController(
            double outputUpperbound,
            double outputLowerbound,
            long sampleTimeInMilliseconds)
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
            curveStartTimeSeconds = double.NaN;
            SetWorkingMode(true);
        }

        public void Stop()
        {
            SetWorkingMode(false);
        }

        public void SetSetpoint(double setpoint)
        {
            this.setpoint = setpoint;
        }

        public void SetTargetCurve(List<double> timeSeconds, List<double> temps)
        {
            if (timeSeconds == null || temps == null || timeSeconds.Count != temps.Count || timeSeconds.Count < 2)
            {
                hasTargetCurve = false;
                targetTimeSeconds.Clear();
                targetTemp.Clear();
                return;
            }

            targetTimeSeconds = new List<double>(timeSeconds);
            targetTemp = new List<double>(temps);
            hasTargetCurve = true;
        }

        public void ClearTargetCurve()
        {
            hasTargetCurve = false;
            targetTimeSeconds.Clear();
            targetTemp.Clear();
        }

        public int Update(double currentTemperatureCelsius, List<double> temperatureHistory, List<double> timeHistory)
        {
            var signal = UpdateSignal(currentTemperatureCelsius, temperatureHistory, timeHistory);
            return signal.Action switch
            {
                ActuatorAction.Heat => 1,
                ActuatorAction.Cool => 2,
                _ => 0,
            };
        }

        public ControlSignal UpdateSignal(double currentTemperatureCelsius, List<double> temperatureHistory, List<double> timeHistory)
        {
            if (!isWorking)
            {
                double currentTimeOff = timeHistory.Count > 0 ? timeHistory[timeHistory.Count - 1] : lastTimeSeconds;
                double targetSetpointOff = GetTargetSetpoint(currentTimeOff);
                double errorOff = targetSetpointOff - currentTemperatureCelsius;
                return new ControlSignal(ActuatorAction.Off, duty: 0.0, output: 0.0, setpoint: targetSetpointOff, error: errorOff);
            }

            double currentTime = timeHistory.Count > 0 ? timeHistory[timeHistory.Count - 1] : lastTimeSeconds;
            double dt = sampleTimeInMilliseconds / 1000.0;
            if (timeHistory.Count > 1)
            {
                double dtCandidate = timeHistory[timeHistory.Count - 1] - timeHistory[timeHistory.Count - 2];
                if (dtCandidate > 0.0)
                    dt = dtCandidate;
            }
            if (dt <= 0.0)
                dt = Math.Max(0.001, sampleTimeInMilliseconds / 1000.0);

            double targetSetpoint = GetTargetSetpoint(currentTime);

            // Si está fuera del rango permitido, forzar actuación al 100% para volver rápido al rango.
            if (currentTemperatureCelsius < lowerBound)
            {
                lastError = targetSetpoint - currentTemperatureCelsius;
                lastTimeSeconds = currentTime;
                return new ControlSignal(ActuatorAction.Heat, duty: 1.0, output: outputUpperbound, setpoint: targetSetpoint, error: lastError);
            }
            if (currentTemperatureCelsius > upperBound)
            {
                lastError = targetSetpoint - currentTemperatureCelsius;
                lastTimeSeconds = currentTime;
                return new ControlSignal(ActuatorAction.Cool, duty: 1.0, output: outputLowerbound, setpoint: targetSetpoint, error: lastError);
            }

            double error = targetSetpoint - currentTemperatureCelsius;

            integral += error * dt;
            double integralLimit = ki > 0.0 ? Math.Abs(outputUpperbound / ki) : 0.0;
            if (integralLimit > 0.0)
                integral = Clamp(integral, -integralLimit, integralLimit);

            double derivative = (error - lastError) / dt;
            double output = kp * error + ki * integral + kd * derivative;
            output = Clamp(output, outputLowerbound, outputUpperbound);

            double deadband = Math.Max(0.25, (upperBound - lowerBound) * 0.05);
            ActuatorAction action;
            double duty;
            if (Math.Abs(output) <= deadband)
            {
                action = ActuatorAction.Off;
                duty = 0.0;
            }
            else
            {
                action = output > 0 ? ActuatorAction.Heat : ActuatorAction.Cool;
                double denom = Math.Abs(outputUpperbound) > 0.000001 ? Math.Abs(outputUpperbound) : 1.0;
                duty = Clamp(Math.Abs(output) / denom, 0.0, 1.0);
            }

            lastError = error;
            lastTimeSeconds = currentTime;

            return new ControlSignal(action, duty, output, targetSetpoint, error);
        }

        double GetTargetSetpoint(double currentTime)
        {
            if (!hasTargetCurve || targetTimeSeconds.Count == 0)
                return setpoint;

            if (double.IsNaN(curveStartTimeSeconds))
                curveStartTimeSeconds = currentTime;

            double t = currentTime - curveStartTimeSeconds;
            if (t <= targetTimeSeconds[0])
                return targetTemp[0];

            int lastIndex = targetTimeSeconds.Count - 1;
            if (t >= targetTimeSeconds[lastIndex])
                return targetTemp[lastIndex];

            for (int i = 1; i < targetTimeSeconds.Count; i++)
            {
                if (t <= targetTimeSeconds[i])
                {
                    double t0 = targetTimeSeconds[i - 1];
                    double t1 = targetTimeSeconds[i];
                    double y0 = targetTemp[i - 1];
                    double y1 = targetTemp[i];
                    double ratio = (t - t0) / (t1 - t0);
                    return y0 + (y1 - y0) * ratio;
                }
            }

            return targetTemp[lastIndex];
        }

        double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
