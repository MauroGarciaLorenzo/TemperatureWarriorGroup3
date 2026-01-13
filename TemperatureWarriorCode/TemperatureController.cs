using Meadow.Gateways.Bluetooth;
// Meadow
using Meadow;
using Meadow.Foundation.Sensors.Temperature;
using Meadow.Devices;
using Meadow.Hardware;
using Meadow.Units;
using System;
using System.Collections.Generic;
using System.Linq;
using MathNet.Numerics.Integration;
using MathNet.Numerics.Interpolation;

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

        // Estado PID sencillo
        double kp = 0.6;
        double ki = 0.2;
        double kd = 0.125;

        double integral = 0.0;
        double derivative = 0.0;
        double lastError = 0.0;

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
            derivative = 0.0;
            lastError = 0.0;
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

        // Llamada desde MeadowApp con la temperatura actual
        public int Update(double currentTemperatureCelsius, List<double> temperatureHistory, List<double> timeHistory)
        {
            int action = 0; // 0: no hacer nada, 1: calentar, 2: enfriar
            if (!isWorking) return 0;

            double currentTime = timeHistory[timeHistory.Count - 1];

            var interp = LinearSpline.Interpolate(timeHistory, temperatureHistory);
            integral = interp.Integrate(currentTime);
            derivative = interp.Differentiate(currentTime);

            // PID discreto muy simple
            double error = setpoint - currentTemperatureCelsius;

            double p = kp * error;
            double i = ki * integral;
            double d = kd * derivative;

            double output = p + i + d;
            if (output > outputUpperbound)
            {
                action = 1;
            }
            else if (output < outputLowerbound)
            {
                action = 2;
            } else
            {
                action = 0;
            }

                lastError = error;

            // Lógica de relés: positivo = calentar, negativo = enfriar
            return action;
        }
    }
}