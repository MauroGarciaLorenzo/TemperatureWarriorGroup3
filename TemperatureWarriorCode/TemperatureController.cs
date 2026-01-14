class TemperatureController
{
    bool isWorking = false;

    double outputUpperbound;
    double outputLowerbound;
    long sampleTimeInMilliseconds;

    double upperBound;
    double lowerBound;
    double setpoint;

    // Estado predictivo
    double lastTemperature = 0.0;
    double lastTimeSeconds = 0.0;
    bool first = true;

    // Ajustes clave
    double hysteresis = 0.35;          // °C
    double predictionHorizon = 8.0;    // segundos
    double minSwitchTime = 10.0;       // segundos

    int lastAction = 0;
    double lastSwitchTime = 0.0;

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
        first = true;
        lastAction = 0;
        lastSwitchTime = 0.0;
        SetWorkingMode(true);
    }

    public void Stop()
    {
        lastAction = 0;
        SetWorkingMode(false);
    }

    public void SetSetpoint(double setpoint)
    {
        this.setpoint = setpoint;
    }

    // 🔁 MISMA FIRMA
    public int Update(
        double currentTemperatureCelsius,
        List<double> temperatureHistory,
        List<double> timeHistory)
    {
        if (!isWorking) return 0;

        double currentTime = timeHistory.Count > 0
            ? timeHistory[timeHistory.Count - 1]
            : lastTimeSeconds + sampleTimeInMilliseconds / 1000.0;

        if (first)
        {
            lastTemperature = currentTemperatureCelsius;
            lastTimeSeconds = currentTime;
            first = false;
            return 0;
        }

        double dt = currentTime - lastTimeSeconds;
        if (dt <= 0.0)
            dt = sampleTimeInMilliseconds / 1000.0;

        // Pendiente térmica (anticipación)
        double dTdt = (currentTemperatureCelsius - lastTemperature) / dt;

        // Predicción
        double predictedTemp =
            currentTemperatureCelsius + dTdt * predictionHorizon;

        int desiredAction = 0;

        if (predictedTemp < lowerBound - hysteresis)
            desiredAction = 1; // calor
        else if (predictedTemp > upperBound + hysteresis)
            desiredAction = 2; // frío

        // Protección de relé / Peltier
        if (desiredAction != lastAction &&
            currentTime - lastSwitchTime < minSwitchTime)
        {
            desiredAction = lastAction;
        }

        if (desiredAction != lastAction)
        {
            lastAction = desiredAction;
            lastSwitchTime = currentTime;
        }

        lastTemperature = currentTemperatureCelsius;
        lastTimeSeconds = currentTime;

        return lastAction;
    }
}