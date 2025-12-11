using Meadow.Gateways.Bluetooth;
// Meadow
using Meadow;
using Meadow.Foundation.Sensors.Temperature;
using Meadow.Devices;
using Meadow.Hardware;
using Meadow.Units;
using System;

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
        double kp = 30.0;
        double ki = 5.0;
        double kd = 2.0;

        double integral = 0.0;
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
        public int Update(double currentTemperatureCelsius)
        {
            int action = 0; // 0: no hacer nada, 1: calentar, 2: enfriar
            if (!isWorking) return 0;

            // PID discreto muy simple
            double error = setpoint - currentTemperatureCelsius;
            double dt = sampleTimeInMilliseconds / 1000.0;

            double p = kp * error;
            integral += error * dt;
            double i = ki * integral;
            double d = kd * (error - lastError) / dt;

            double output = p + i + d;

            lastError = error;

            // Lógica de relés: positivo = calentar, negativo = enfriar
            if (currentTemperatureCelsius < setpoint - (upperBound-lowerBound) / 4)
            {
                action = 1;
            }
            if (currentTemperatureCelsius > setpoint + (upperBound - lowerBound) / 4)
            {
                action = 2;
            }
            return action;
        }
    }
}