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


        bool isWorking = false;
        double outputUpperbound;
        double outputLowerbound;
        long sampleTimeInMilliseconds;
        double upperBound;
        double lowerBound;
        double setpoint;

        // Estado PID para control ON/OFF con inercia
        // Kp: Reacción, Ki: Corrección mínima, Kd: Freno fuerte
        double kp = 25.0;
        double ki = 0.1;
        double kd = 60.0;

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
            int action = 0; // 0: no hacer nada, 1: calentar, 2: enfriar
            if (!isWorking) return 0;

            double currentTime = timeHistory.Count > 0 ? timeHistory[timeHistory.Count - 1] : lastTimeSeconds;
            double dt = sampleTimeInMilliseconds / 1000.0;
            if (timeHistory.Count > 1)
            {
                double dtCandidate = timeHistory[timeHistory.Count - 1] - timeHistory[timeHistory.Count - 2];
                if (dtCandidate > 0.0)
                    dt = dtCandidate;
            }

            // PID discreto muy simple
            double error = setpoint - currentTemperatureCelsius;

            // Bypasseamos el control Bang-Bang para que el PID controle la aproximación suavemente
            /*
            if (currentTemperatureCelsius < lowerBound) ...
            */

            integral += error * dt;
            double integralLimit = ki > 0.0 ? Math.Abs(outputUpperbound / ki) : 0.0;
            if (integralLimit > 0.0)
            {
                integral = Clamp(integral, -integralLimit, integralLimit);
            }

            double derivative = (error - lastError) / dt;

            double p = kp * error;
            double i = ki * integral;
            double d = kd * derivative;

            double output = p + i + d;
            output = Clamp(output, outputLowerbound, outputUpperbound);

            double deadband = 2.0;
            if (output > deadband)
                action = 1;
            else if (output < -deadband)
                action = 2;
            else
                action = 0;
            // Permitimos negativos para control simétrico (calentar/enfriar)
            output = Clamp(output, -outputUpperbound, outputUpperbound);

            

            // Lógica de relés: positivo = calentar, negativo = enfriar
            return action;
        }

        double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
