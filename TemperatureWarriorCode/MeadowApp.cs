// Meadow
using Meadow;
using Meadow.Foundation.Sensors.Temperature;
using Meadow.Devices;
using Meadow.Hardware;
using Meadow.Units;

// C#
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

// DOSS
using TemperatureWarriorCode.Web;
using NETDuinoWar;

// RingBuffer.NET
using RingBuffer;
using System.Collections.Generic;


namespace TemperatureWarriorCode
{

    public class MeadowApp : App<F7FeatherV2>
    {

        // Sensor de temperatura
        AnalogTemperature sensor;
        TimeSpan sensorSampleTime = TimeSpan.FromMilliseconds(50);
        Temperature currentTemperature;
        double displayTemperatureCelsius = double.NaN;
        double controlTemperatureCelsius = double.NaN;
        double lastControlTemperatureCelsius = double.NaN;
        long lastActuatorChangeMs = 0;
        List<double> temperatureHistory = new List<double>();
        List<double> timeHistory = new List<double>();
        int numberOfPoints = 0;

        readonly int actuatorBlankingMs = 600;
        readonly int minSamplesBeforeControl = 15;

        // Filtro de temperatura para el PID (anti-ruido/outliers)
        // - Mediana de una ventana pequeña para eliminar picos
        // - Limitador de velocidad para impedir saltos imposibles entre muestras
        readonly int controlMedianWindowSize = 7;
        readonly double outlierDistanceFromMedianCelsius = 3.0;
        readonly double maxFilteredRateCelsiusPerSecond = 5.0;
        readonly Queue<double> recentRawTemperatureCelsius = new Queue<double>();
        double? lastFilteredTemperatureCelsius = null;
        long lastFilteredTemperatureMillis = 0;
        

        TemperatureController temperatureController;
        bool temperatureHandlerRunning = false; // Evitar overlapping de handlers

        // Estado del actuador en un rango de temperatura
        
        double currentSetpoint;
        TemperatureRange currentRange;

        IDigitalOutputPort coolingRelayPort;
        IDigitalOutputPort heatingRelayPort;

        // Cancelación de ronda en curso
        CancellationTokenSource shutdownCancellationSource = new();
        enum CancellationReason
        {
            ShutdownCommand,
            TempTooHigh,
            ConnectionLost,
        }
        CancellationReason cancellationReason;

        // Estado inter-comando para la librería de registro de temperatura
        int totalOperationTimeInMilliseconds = 0;
        int totalTimeInRangeInMilliseconds = 0;
        int totalTimeOutOfRangeInMilliseconds = 0;

        // El comando a ejecutar
        Command? currentCommand;

        // Buffer de actualizaciones a enviar en la próxima notificación al cliente
        RingBuffer<double> nextNotificationsBuffer = new(10);
        readonly long notificationPeriodInMilliseconds = 800;
        readonly int anticipationSeconds = 5;

        // El modo de ejecución del sistema
        enum OpMode
        {
            Config, // Parámetros de ronda no configurados
            Prep, // Parámetros de ronda configurados, esperando comando de inicio de combate
            Combat, // Ejecutando ronda
        }
        OpMode currentMode = OpMode.Config;

        public override async Task Run()
        {
            Resolver.Log.Info("[MeadowApp] ### Init: Run() ###");

            // Configurar sensores
            SensorSetup();

            // Configurar modo inicial ('esperando configuración')
            currentMode = OpMode.Config;

            await LaunchNetworkAndWebserver();

            Resolver.Log.Info("[MeadowApp] ### Fin: Run() ###");
            return;
        }

        /// <summary>
        /// 
        /// </summary>
        private void SensorSetup()
        {
            // TODO Inicializar sensores de actuadores

            temperatureController =
                new TemperatureController(outputUpperbound: 255.0, outputLowerbound: -255.0,
                                        sampleTimeInMilliseconds: sensorSampleTime.Milliseconds);

            // Configuración de Sensor de Temperatura
            sensor = new AnalogTemperature(analogPin: Device.Pins.A02, sensorType: AnalogTemperature.KnownSensorType.LM35);

            // Configuración de pines de relés
            coolingRelayPort = Device.CreateDigitalOutputPort(
                Device.Pins.D10, 
                initialState: true
            );
            heatingRelayPort = Device.CreateDigitalOutputPort(
                Device.Pins.D11, 
                initialState: true
            );

            sensor.Updated += TemperatureUpdateHandler;
            // Muestreo rápido del sensor; las notificaciones al cliente siguen con su propia cadencia.
            sensor.StartUpdating(sensorSampleTime);
        }

        private async Task LaunchNetworkAndWebserver()
        {
            // Configuración de Red
            var wifi = Device.NetworkAdapters.Primary<IWiFiNetworkAdapter>();
            if (wifi is null)
            {
                Resolver.Log.Info($"ERROR: No se pudo localizar la interfaz de red primaria");
                return;
            }

            Resolver.Log.Info("[MeadowApp] Connecting to WiFi ...");
            wifi.Connect(Secrets.WIFI_NAME, Secrets.WIFI_PASSWORD).Wait();
            //if (!wifi.IsConnected)
            //{
            //    Resolver.Log.Info($"ERROR: No se pudo establecer conexión a SSID: {Secrets.WIFI_NAME}");
            //    return;
            //}

            wifi.NetworkConnected += async (networkAdapter, networkConnectionEventArgs) =>
            {
                Resolver.Log.Info($"[MeadowApp] Connected to WiFi -> {networkAdapter.IpAddress}");

                // Lanzar Servidor de Comandos
                WebSocketServer webServer = new(wifi.IpAddress, Config.Port);
                if (webServer is null) {
                    Resolver.Log.Info("[MeadowApp] ERROR: Failed to create a WebSocketServer instance");
                    return;
                }
                webServer.MessageReceived += MessageHandler;
                webServer.ConnectionFinished += ConnectionFinishedHandler;
                await webServer.Start();
            };            
        }

        private void Shutdown(CancellationReason reason)
        {
            cancellationReason = reason;
            shutdownCancellationSource.Cancel();
            shutdownCancellationSource = new CancellationTokenSource();
        }

        private void ConnectionFinishedHandler(WebSocketServer webServer, NetworkStream connection)
        {
            Shutdown(CancellationReason.ConnectionLost);
        }



        private void TemperatureTooHighHandler()
        {
            Shutdown(CancellationReason.TempTooHigh);
        }


        private void TemperatureUpdateHandler(object sender, IChangeResult<Temperature> e)
        {

            var nowMillis = TimeUtils.millis();

            // BLOQUEO tras cambio de actuador
            if (nowMillis - lastActuatorChangeMs < actuatorBlankingMs)
            {
                // Actualiza solo display si quieres, pero NO control ni histórico
                displayTemperatureCelsius = lastFilteredTemperatureCelsius ?? e.New.Celsius;
                return;
            }
            var candidateC = e.New.Celsius;

            static double Clamp(double value, double min, double max)
            {
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }

            if (double.IsNaN(candidateC) || double.IsInfinity(candidateC))
                return;

            // Mantener el comportamiento previo para lecturas inválidas negativas (modo demo/test)
            if (candidateC < 0)
            {
                Random rnd = new Random();
                candidateC = rnd.Next(minValue: 20, maxValue: 21);
            }

            // Actualizar ventana de muestras crudas
            recentRawTemperatureCelsius.Enqueue(candidateC);
            while (recentRawTemperatureCelsius.Count > controlMedianWindowSize)
                recentRawTemperatureCelsius.Dequeue();

            // Mediana (robusta ante outliers)
            var window = recentRawTemperatureCelsius.ToArray();
            Array.Sort(window);
            var medianC = window[window.Length / 2];

            // Si esta muestra está muy lejos de la mediana, usar la mediana para el PID
            var filteredC = (Math.Abs(candidateC - medianC) > outlierDistanceFromMedianCelsius)
                ? medianC
                : candidateC;

            // Limitador de velocidad de cambio para evitar saltos imposibles entre muestras
            if (lastFilteredTemperatureCelsius.HasValue)
            {
                var dtSeconds = Math.Max(0.001, (nowMillis - lastFilteredTemperatureMillis) / 1000.0);
                var maxDelta = maxFilteredRateCelsiusPerSecond * dtSeconds;
                filteredC = Clamp(filteredC,
                    lastFilteredTemperatureCelsius.Value - maxDelta,
                    lastFilteredTemperatureCelsius.Value + maxDelta);
            }

            lastFilteredTemperatureCelsius = filteredC;
            lastFilteredTemperatureMillis = nowMillis;

            displayTemperatureCelsius = filteredC;
            if (double.IsNaN(lastControlTemperatureCelsius))
                lastControlTemperatureCelsius = displayTemperatureCelsius;

            // Mantener la temperatura visible como la filtrada
            currentTemperature = new Temperature(displayTemperatureCelsius);
            temperatureHistory.Add(displayTemperatureCelsius);
            timeHistory.Add(nowMillis / 1000.0);

            Resolver.Log.Info($"[MeadowApp] DEBUG (Remove this console line): Current temperature={currentTemperature.Celsius}");

            TemperatureControllerHandler();
        }

        private void TemperatureControllerHandler()
        {
            if (temperatureHistory.Count < minSamplesBeforeControl)
                return;
            if (temperatureHandlerRunning)
                return;
            temperatureHandlerRunning = true; 

            var currTemp = currentTemperature;
            var nowMs = TimeUtils.millis();

            // TODO Gestionar controlador de temperatura si estamos en modo combate
            // Solo controlar en modo combate y si no es test
            // if (currentMode == OpMode.Combat && currentCommand is { isTest: false })
            // {
            if (!double.IsNaN(lastControlTemperatureCelsius) && nowMs - lastActuatorChangeMs < 400)
                controlTemperatureCelsius = lastControlTemperatureCelsius;
            else
                controlTemperatureCelsius = displayTemperatureCelsius;

            int action = temperatureController.Update(controlTemperatureCelsius, temperatureHistory, timeHistory);
            lastControlTemperatureCelsius = controlTemperatureCelsius;
            if (action == 1)
            {
                heat();
            } else if (action == 2)
            {
                cool();
            } else
            {
                shutdown();
            }

            // Protección por temperatura demasiado alta
            if (currTemp.Celsius > 220)
            {
                Resolver.Log.Info($"[MeadowApp] ALERTA: Temperatura crítica: {currTemp.Celsius}ºC");
                TemperatureTooHighHandler();
            }
            // }

            temperatureHandlerRunning = false;
        }

        private async void MessageHandler(WebSocketServer webServer, NetworkStream connection, Message message)
        {
            switch (message.type)
            {
                case Message.MessageType.Command:
                    {
                        // No se puede cambiar el comando a la mitad de una ronda en curso
                        if (currentMode == OpMode.Combat)
                        {
                            Resolver.Log.Info("[MeadowApp] Esperar a la finalización de la ronda en curso");
                            await webServer.SendMessage(connection, "{\"type\": \"StateError\"}");
                            return;
                        }
                        if (!message.data.HasValue || !message.data.Value.IsValid())
                        {
                            Resolver.Log.Info("[MeadowApp] Comando inválido");
                            await webServer.SendMessage(connection, "{\"type\": \"Bad Format\"}");
                            return;
                        }

                        Resolver.Log.Info("[MeadowApp] Comando guardado");
                        currentCommand = message.data.Value.ToCommand();
                        currentMode = OpMode.Prep;
                        await webServer.SendMessage(connection, "{\"type\": \"ConfigOK\"}");
                        break;
                    }
                case Message.MessageType.Start:
                    {
                        if (!currentCommand.HasValue)
                        {
                            Resolver.Log.Info("[MeadowApp] Configurar comando antes de iniciar ejecución");
                            await webServer.SendMessage(connection, "{\"type\": \"StateError\"}");
                            return;
                        }
                        if (currentMode == OpMode.Combat)
                        {
                            Resolver.Log.Info("[MeadowApp] No se puede lanzar una ronda mientras otra está en curso");
                            await webServer.SendMessage(connection, "{\"type\": \"StateError\"}");
                            return;
                        }

                        Resolver.Log.Info("[MeadowApp] Iniciando ejecución de comando");
                        currentMode = OpMode.Combat;
                        await StartRound(webServer, connection);
                        currentCommand = null;
                        currentMode = OpMode.Config;
                        break;
                    }
                case Message.MessageType.Shutdown:
                    {
                        Resolver.Log.Info("[MeadowApp] Shutdown recibido");
                        Shutdown(CancellationReason.ShutdownCommand);
                        break;
                    }
                default:
                    {
                        await webServer.SendMessage(connection, "{\"type\": \"Bad Format\"}");
                        break;
                    }
            }
        }

        private string SerializeNextNotifications()
        {
            return JsonSerializer.Serialize(nextNotificationsBuffer,
                                                     new JsonSerializerOptions
                                                     {
                                                         Converters = { new RingBufferJsonConverter() },
                                                         WriteIndented = true
                                                     });

        }

        private Task NotifyClient(WebSocketServer webServer, NetworkStream connection)
        {
            return webServer.SendMessage(connection, $"{{ \"type\": \"N\", \"ns\": {SerializeNextNotifications()}}}");
        }

        private void RegisterTimeControllerTemperature(TimeController timeController)
        {
            var currTemp = double.IsNaN(displayTemperatureCelsius)
                ? currentTemperature.Celsius
                : displayTemperatureCelsius;
            timeController.RegisterTemperature(currTemp);
            try
            {
                if (!nextNotificationsBuffer.Enqueue(currTemp))
                    Resolver.Log.Info("[MeadowApp] Fallo en añadir a cola de notifiaciones");
            }
            catch
            {
                Resolver.Log.Info("[MeadowApp] Fallo en añadir a cola de notifiaciones");
            }
        }



        //TW Combat Round
        private async Task StartRound(WebSocketServer webServer, NetworkStream connection)
        {
            Resolver.Log.Info("[MeadowApp] ### Init: StartRound() ###");
            if (currentCommand is null)
            {
                throw new NullReferenceException("currentCommand no puede ser null al comenzar StartRound");
            }

            var cmd = currentCommand.Value;

            int totalRoundOperationTimeInMilliseconds = cmd.temperatureRanges.Aggregate(0, (acc, range) => acc + range.RangeTimeInMilliseconds);

            // Inicialización de librería de control
            TimeController timeController = new()
            {
                DEBUG_MODE = false
            };

            if (!timeController.Configure(cmd.temperatureRanges, totalRoundOperationTimeInMilliseconds, cmd.refreshInMilliseconds, out string error))
            {
                Resolver.Log.Info($"[MeadowApp] Error configurando controlador de tiempo >>> {error}");
                await webServer.SendMessage(connection, "{\"type\": \"TimeControllerConfigError\"}");
                return;
            }

            var shutdownCancellationToken = shutdownCancellationSource.Token;

            double getRangeSetpoint(TemperatureRange range) => range.MinTemp + (range.MaxTemp - range.MinTemp) * 0.5;

            int anticipationMs = Math.Max(0, anticipationSeconds * 1000);

            int GetRangeIndexAtTime(TemperatureRange[] ranges, int timeMs)
            {
                if (ranges is null || ranges.Length == 0)
                    return 0;
                if (timeMs <= 0)
                    return 0;

                var remaining = timeMs;
                for (int i = 0; i < ranges.Length; i++)
                {
                    var duration = ranges[i].RangeTimeInMilliseconds;
                    if (remaining < duration)
                        return i;
                    remaining -= duration;
                }

                return ranges.Length - 1;
            }

            if (!cmd.isTest)
            {
                TemperatureRange firstRange = cmd.temperatureRanges.First();
                var anticipatedRangeIndexAtStart = GetRangeIndexAtTime(cmd.temperatureRanges, anticipationMs);
                TemperatureRange anticipatedRangeAtStart = cmd.temperatureRanges[anticipatedRangeIndexAtStart];

                // Bounds siguen el rango "actual" (por tiempo real). El setpoint se adelanta.
                currentSetpoint = getRangeSetpoint(anticipatedRangeAtStart);
                temperatureController.SetSetpoint(currentSetpoint);
                temperatureController.setBounds(lowerBound: firstRange.MinTemp, upperBound: firstRange.MaxTemp);
                temperatureController.Start();
            }

            //// Acomodar tamaño de ringbuffer y zero-out ringbuffer
            // Debemos ser capaces de ingresar ceil(notificationPeriodInMilliseconds / cmd.refreshInMilliseconds),
            // además multiplicamos este resultado por 3 para dar algo de "wiggle room".
            var newSize = 10 * (int)Math.Ceiling(notificationPeriodInMilliseconds / (double)cmd.refreshInMilliseconds);
            nextNotificationsBuffer.ResizeAndReset(newSize);

            //// Lanzar conteo en librería de control cada refreshInMilliseconds
            void registerTimeController(object _) => RegisterTimeControllerTemperature(timeController);
            timeController.StartOperation();
            Timer registerTimer = new(registerTimeController, null, 0, cmd.refreshInMilliseconds);

            // Enviar primera temperatura medida
            RegisterTimeControllerTemperature(timeController);

            //// Notificaciones al cliente
            Timer notificationTimer = new(async _ => await NotifyClient(webServer, connection), null, 0, notificationPeriodInMilliseconds);

            // Planificación de rangos:
            // - Los bounds (setBounds) cambian en el inicio real del rango.
            // - El setpoint (SetSetpoint) se adelanta: usa el rango que estará activo en (t + anticipation).
            var ranges = cmd.temperatureRanges;
            var rangeStartMs = new int[ranges.Length];
            var accMs = 0;
            for (int i = 0; i < ranges.Length; i++)
            {
                rangeStartMs[i] = accMs;
                accMs += ranges[i].RangeTimeInMilliseconds;
            }

            var setpointRangeIndex = GetRangeIndexAtTime(ranges, anticipationMs);
            var nextSetpointBoundaryIndex = Math.Min(ranges.Length, setpointRangeIndex + 1);
            var elapsedMs = 0;

            // Asegurar setpoint inicial también durante tests (no activa relés porque Start() no se llama)
            currentSetpoint = getRangeSetpoint(ranges[setpointRangeIndex]);
            temperatureController.SetSetpoint(currentSetpoint);

            for (int rangeIndex = 0; rangeIndex < ranges.Length; rangeIndex++)
            {
                var range = ranges[rangeIndex];
                currentRange = range;
                temperatureController.setBounds(lowerBound: range.MinTemp, upperBound: range.MaxTemp);
                Resolver.Log.Info($"Iniciando rango [{range.MinTemp} - {range.MaxTemp}]");

                var rangeEndMs = rangeStartMs[rangeIndex] + range.RangeTimeInMilliseconds;

                // Aplicar todos los cambios de setpoint cuya "hora adelantada" cae dentro de este rango real.
                while (nextSetpointBoundaryIndex < ranges.Length)
                {
                    var switchTimeMs = rangeStartMs[nextSetpointBoundaryIndex] - anticipationMs;
                    if (switchTimeMs >= rangeEndMs)
                        break;

                    var delayMs = switchTimeMs - elapsedMs;
                    if (delayMs > 0)
                    {
                        try
                        {
                            await Task.Delay(delayMs, shutdownCancellationToken);
                        }
                        catch (TaskCanceledException)
                        {
                            break;
                        }
                        elapsedMs = switchTimeMs;
                    }

                    // En este instante, (t + anticipation) cruza al siguiente rango: actualizar setpoint.
                    currentSetpoint = getRangeSetpoint(ranges[nextSetpointBoundaryIndex]);
                    temperatureController.SetSetpoint(currentSetpoint);
                    nextSetpointBoundaryIndex++;
                }

                if (shutdownCancellationToken.IsCancellationRequested)
                    break;

                var remainingMs = rangeEndMs - elapsedMs;
                if (remainingMs > 0)
                {
                    try
                    {
                        await Task.Delay(remainingMs, shutdownCancellationToken);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                    elapsedMs = rangeEndMs;
                }
            }

            // Apagar actuadores y desactivar timers/librería de registro de temp
            notificationTimer.Dispose();
            registerTimer.Dispose();

            // Cuando el tiempo de operación de la ronda es divisible por el tiempo de refresco, se pierde
            // la última medición
            if (totalRoundOperationTimeInMilliseconds / cmd.refreshInMilliseconds == 0)
                RegisterTimeControllerTemperature(timeController);

            if (!cmd.isTest)
            { // Apagar actuador en caso de no ser un test de sensor de temperatura
                temperatureController.Stop();
                Thread.Sleep(100);
                
            }

            if (shutdownCancellationToken.IsCancellationRequested)
            { // Notificar finalización por altas temperaturas
                switch (cancellationReason)
                {
                    case CancellationReason.TempTooHigh:
                        {
                            await webServer.SendMessage(connection, $"{{ \"type\": \"TempTooHigh\", \"message\": \"High Temperature Emergency Stop {currentTemperature}\" }}");
                            break;
                        }
                    case CancellationReason.ShutdownCommand:
                        {
                            await webServer.SendMessage(connection, $"{{ \"type\": \"ShutdownCommand\", \"message\": \"Shutdown Command Received\" }}");
                            break;
                        }
                    case CancellationReason.ConnectionLost:
                        {
                            break;
                        }
                }
            }
            else
            { // Calcular resultados
                if (!cmd.isTest)
                {
                    // Solamente actualizar estado inter-ronda si no se trata de un test de sensores
                    totalTimeInRangeInMilliseconds += timeController.TimeInRangeInMilliseconds;
                    totalTimeOutOfRangeInMilliseconds += timeController.TimeOutOfRangeInMilliseconds;
                    totalOperationTimeInMilliseconds += totalRoundOperationTimeInMilliseconds;
                    Resolver.Log.Info($"Global - Tiempo dentro del rango {totalTimeInRangeInMilliseconds} ms de {totalOperationTimeInMilliseconds}s");
                    Resolver.Log.Info($"Global - Tiempo fuera del rango {totalTimeOutOfRangeInMilliseconds} ms de {totalOperationTimeInMilliseconds}s");
                }

                Resolver.Log.Info($"Ronda - Tiempo dentro del rango {timeController.TimeInRangeInMilliseconds} ms de {totalRoundOperationTimeInMilliseconds} ms");
                Resolver.Log.Info($"Ronda - Tiempo fuera del rango {timeController.TimeOutOfRangeInMilliseconds} ms de {totalRoundOperationTimeInMilliseconds} ms");

                // Indicar finalización y enviar datos de refresco restantes en el buffer
                await webServer.SendMessage(connection, $"{{ \"type\": \"RoundFinished\", \"timeInRange\": {timeController.TimeInRangeInMilliseconds}, \"ns\": {SerializeNextNotifications()}}}");
            }

            timeController.FinishOperation();

            Resolver.Log.Info("[MeadowApp] ### Fin: StartRound() ###");
            return;
        }

        public void heat()
        {
            var changed = heatingRelayPort.State != true || coolingRelayPort.State != false;

            heatingRelayPort.State = true;
            coolingRelayPort.State = false;

            if (changed)
                lastActuatorChangeMs = TimeUtils.millis();
        }

        public void cool()
        {
            var changed = coolingRelayPort.State != true || heatingRelayPort.State != false;

            coolingRelayPort.State = true;
            heatingRelayPort.State = false;

            if (changed)
                lastActuatorChangeMs = TimeUtils.millis();
        }

        public void shutdown()
        {
            var changed = heatingRelayPort.State != true || coolingRelayPort.State != true;

            heatingRelayPort.State = true;
            coolingRelayPort.State = true;

            if (changed)
                lastActuatorChangeMs = TimeUtils.millis();
        }
            
    }


    // Serialización de ringbuffer de notificaciones
    public class RingBufferJsonConverter : JsonConverter<RingBuffer<double>>
    {
        public override RingBuffer<double> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            throw new NotImplementedException("Deserialización no implementada");
        }

        public override void Write(Utf8JsonWriter writer, RingBuffer<double> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            while (value.Dequeue(out double item))
            {
                writer.WriteNumberValue(Math.Round(item, 2));
            }
            writer.WriteEndArray();
        }
    }

}
