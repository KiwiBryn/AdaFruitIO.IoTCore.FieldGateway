//---------------------------------------------------------------------------------
// Copyright (c) December 2017, devMobile Software
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
// Thanks to the creators and maintainers of the libraries used by this project
//    https://github.com/RSuter/NSwag
//    https://github.com/techfooninja/Radios.RF24
//---------------------------------------------------------------------------------
namespace devMobile.AdaFruitIO.IoTCore.FieldGateway.NRF24L01
{
   using System;
   using System.Diagnostics;
   using System.IO;
   using System.Text;
   using System.Threading.Tasks;
   using AdaFruit.IO;
   using Newtonsoft.Json;
   using Newtonsoft.Json.Converters;
   using Radios.RF24;
   using Windows.ApplicationModel.Background;
   using Windows.Foundation.Diagnostics;
   using Windows.Storage;

   public sealed class StartupTask : IBackgroundTask
   {
      private const string ConfigurationFilename = "config.json";

      private const byte MessageHeaderPosition = 0;
      private const byte MessageHeaderLength = 1;

      // nRF24 Hardware interface configuration
      private const byte RF24ModuleChipEnablePin = 25;
      private const byte RF24ModuleChipSelectPin = 0;
      private const byte RF24ModuleInterruptPin = 17;

      private readonly LoggingChannel loggingChannel = new LoggingChannel("devMobile AdaFruit.IO nRF24L01 Field Gateway", null, new Guid("4bd2826e-54a1-4ba9-bf63-92b73ea1ac4a"));
      private readonly RF24 rf24 = new RF24();
      private readonly AdaFruit.IO.Client adaFruitIOClient = new AdaFruit.IO.Client();
      private ApplicationSettings applicationSettings = null;
      private BackgroundTaskDeferral deferral;

      private enum MessagePayloadType : byte
      {
         Echo = 0,
         DeviceIdPlusCsvSensorReadings,
      }

      public void Run(IBackgroundTaskInstance taskInstance)
      {
         if (!this.ConfigurationFileLoad().Result)
         {
            return;
         }

         // Configure the AdaFruit API client
         LoggingFields adaFruitIOSettings = new LoggingFields();
         if (!string.IsNullOrEmpty(this.applicationSettings.AdaFruitIOBaseUrl))
         {
            this.adaFruitIOClient.BaseUrl = this.applicationSettings.AdaFruitIOBaseUrl;
            adaFruitIOSettings.AddString("BaseURL", this.applicationSettings.AdaFruitIOBaseUrl);
         }

         this.adaFruitIOClient.ApiKey = this.applicationSettings.AdaFruitIOApiKey;

         adaFruitIOSettings.AddString("APIKey", this.applicationSettings.AdaFruitIOApiKey);
         adaFruitIOSettings.AddString("UserName", this.applicationSettings.AdaFruitIOUserName);
         adaFruitIOSettings.AddString("GroupName", this.applicationSettings.AdaFruitIOGroupName);
         this.loggingChannel.LogEvent("AdaFruit.IO configuration", adaFruitIOSettings, LoggingLevel.Information);

         // Configure the nRF24L01 module
         this.rf24.OnDataReceived += this.Radio_OnDataReceived;
         this.rf24.OnTransmitFailed += this.Radio_OnTransmitFailed;
         this.rf24.OnTransmitSuccess += this.Radio_OnTransmitSuccess;

         this.rf24.Initialize(RF24ModuleChipEnablePin, RF24ModuleChipSelectPin, RF24ModuleInterruptPin);
         this.rf24.Address = Encoding.UTF8.GetBytes(this.applicationSettings.RF24Address);
         this.rf24.Channel = this.applicationSettings.RF24Channel;

         // The order of setting the power level and Data rate appears to be important, most
         // probably register masking issue in RF24 library which needs some further investigation
         this.rf24.PowerLevel = this.applicationSettings.RF24PowerLevel;
         this.rf24.DataRate = this.applicationSettings.RF24DataRate;
         this.rf24.IsAutoAcknowledge = this.applicationSettings.IsRF24AutoAcknowledge;
         this.rf24.IsDyanmicAcknowledge = this.applicationSettings.IsRF24DynamicAcknowledge;
         this.rf24.IsDynamicPayload = this.applicationSettings.IsRF24DynamicPayload;
         this.rf24.IsEnabled = true;

         LoggingFields rf24Settings = new LoggingFields();
         rf24Settings.AddUInt8("Channel", this.applicationSettings.RF24Channel);
         rf24Settings.AddString("DataRate", this.applicationSettings.RF24DataRate.ToString());
         rf24Settings.AddString("Address", this.applicationSettings.RF24Address);
         rf24Settings.AddString("PowerLevel", this.applicationSettings.RF24PowerLevel.ToString());
         rf24Settings.AddBoolean("AutoAcknowledge", this.applicationSettings.IsRF24AutoAcknowledge);
         rf24Settings.AddBoolean("DynamicAcknowledge", this.applicationSettings.IsRF24DynamicAcknowledge);
         rf24Settings.AddBoolean("DynamicPayload", this.applicationSettings.IsRF24DynamicPayload);
         this.loggingChannel.LogEvent("nRF24L01 configuration", rf24Settings, LoggingLevel.Information);

         this.deferral = taskInstance.GetDeferral();
      }

      private async Task<bool> ConfigurationFileLoad()
      {
         StorageFolder localFolder = ApplicationData.Current.LocalFolder;

         // Check to see if file exists
         if (localFolder.TryGetItemAsync(ConfigurationFilename).GetAwaiter().GetResult() == null)
         {
            this.loggingChannel.LogMessage("Configuration file " + ConfigurationFilename + " not found", LoggingLevel.Error);

            this.applicationSettings = new ApplicationSettings()
            {
               AdaFruitIOBaseUrl = "AdaFruit Base URL can go here",
               AdaFruitIOUserName = "AdaFruit User name goes here",
               AdaFruitIOApiKey = "AdaFruitIO API Ket goes here",
               AdaFruitIOGroupName = "AdaFruit Group name goes here",
               RF24Address = "Base1",
               RF24Channel = 15,
               RF24DataRate = DataRate.DR250Kbps,
               RF24PowerLevel = PowerLevel.High,
               IsRF24AutoAcknowledge = true,
               IsRF24DynamicAcknowledge = false,
               IsRF24DynamicPayload = true,
            };

            // Create empty configuration file
            StorageFile configurationFile = await localFolder.CreateFileAsync(ConfigurationFilename, CreationCollisionOption.OpenIfExists);
            using (Stream stream = await configurationFile.OpenStreamForWriteAsync())
            {
               using (TextWriter streamWriter = new StreamWriter(stream))
               {
                  streamWriter.Write(JsonConvert.SerializeObject(this.applicationSettings, Formatting.Indented));
               }
            }

            return false;
         }

         try
         {
            // Load the configuration settings
            StorageFile configurationFile = (StorageFile)await localFolder.TryGetItemAsync(ConfigurationFilename);

            using (Stream stream = await configurationFile.OpenStreamForReadAsync())
            {
               using (StreamReader streamReader = new StreamReader(stream))
               {
                  this.applicationSettings = JsonConvert.DeserializeObject<ApplicationSettings>(streamReader.ReadToEnd());
               }
            }

            return true;
         }
         catch (Exception ex)
         {
            this.loggingChannel.LogMessage("Configuration file " + ConfigurationFilename + " load failed " + ex.Message, LoggingLevel.Error);
            return false;
         }
      }

      private void Radio_OnDataReceived(byte[] messageData)
      {
         // Check the payload is long enough to contain header length
         if (messageData.Length < MessageHeaderLength)
         {
            this.loggingChannel.LogMessage("Message too short to contain header", LoggingLevel.Warning);
            return;
         }

         // Extract the top nibble of header byte which is message type
         switch ((MessagePayloadType)(messageData[MessageHeaderPosition] >> 4))
         {
            case MessagePayloadType.Echo:
               this.MessageDataDisplay(messageData);
               break;
            case MessagePayloadType.DeviceIdPlusCsvSensorReadings:
               this.MessageDataDeviceIdPlusCsvSensorData(messageData);
               break;
            default:
               this.MessageDataDisplay(messageData);
               break;
         }
      }

      private void Radio_OnTransmitSuccess()
      {
         this.loggingChannel.LogMessage("Transmit Succeeded");
         Debug.WriteLine("Transmit Succeeded");
      }

      private void Radio_OnTransmitFailed()
      {
         this.loggingChannel.LogMessage("Transmit failed");
         Debug.WriteLine("Transmit Failed");
      }

      private void MessageDataDisplay(byte[] messageData)
      {
         string bcdText = BitConverter.ToString(messageData);
         string unicodeText = Encoding.UTF8.GetString(messageData);

         Debug.WriteLine("Length:{0} BCD:{1} Unicode:{2}", messageData.Length, bcdText, unicodeText);

         LoggingFields messagePayload = new LoggingFields();
         messagePayload.AddInt32("Length", messageData.Length);
         messagePayload.AddString("BCD", bcdText);
         messagePayload.AddString("Unicode", unicodeText);
         this.loggingChannel.LogEvent("Message Data", messagePayload, LoggingLevel.Verbose);
      }

      private async void MessageDataDeviceIdPlusCsvSensorData(byte[] messageData)
      {
         char[] sensorReadingSeparator = new char[] { ',' };
         char[] sensorIdAndValueSeparator = new char[] { ' ' };

         // Mask off lower nibble of message header, which is device ID length
         byte deviceIdLength = (byte)(messageData[MessageHeaderPosition] & (byte)0b1111);

         // Check the payload is long enough to contain the header & specified SensorDeviceID length
         if (messageData.Length < MessageHeaderLength + deviceIdLength)
         {
            this.loggingChannel.LogMessage("Message data too short to contain device identifier", LoggingLevel.Warning);
            return;
         }

         string deviceId = BitConverter.ToString(messageData, MessageHeaderLength, deviceIdLength).ToLower();

         // Check that there is a payload
         if (messageData.Length <= MessageHeaderLength + deviceIdLength)
         {
            this.loggingChannel.LogMessage("Message data too short to contain any sensor readings", LoggingLevel.Warning);
            return;
         }

         // Copy the payload across to a string where it can be chopped up
         string payload = Encoding.UTF8.GetString(messageData, MessageHeaderLength + deviceIdLength, messageData.Length - (MessageHeaderLength + deviceIdLength));

         Debug.WriteLine("{0:hh:mm:ss} Address {1} Length {2} Payload {3} Length {4}", DateTime.UtcNow, deviceId, deviceIdLength, payload, payload.Length);

         // Chop up the CSV text payload
         string[] sensorReadings = payload.Split(sensorReadingSeparator, StringSplitOptions.RemoveEmptyEntries);
         if (sensorReadings.Length == 0)
         {
            this.loggingChannel.LogMessage("Payload contains no sensor readings", LoggingLevel.Warning);
            return;
         }

         Group_feed_data groupFeedData = new Group_feed_data();

         LoggingFields sensorData = new LoggingFields();
         sensorData.AddString("DeviceID", deviceId);

         // Chop up each sensor reading into an ID & value
         foreach (string sensorReading in sensorReadings)
         {
            string[] sensorIdAndValue = sensorReading.Split(sensorIdAndValueSeparator, StringSplitOptions.RemoveEmptyEntries);

            // Check that there is an id & value
            if (sensorIdAndValue.Length != 2)
            {
               this.loggingChannel.LogMessage("Sensor reading invalid format", LoggingLevel.Warning);
               return;
            }

            string sensorId = sensorIdAndValue[0].ToLower();
            string value = sensorIdAndValue[1];

            // Construct the sensor ID from SensordeviceID & Value ID
            groupFeedData.Feeds.Add(new Anonymous2() { Key = string.Format("{0}{1}", deviceId, sensorId), Value = value });

            sensorData.AddString(sensorId, value);

            Debug.WriteLine(" Sensor {0}{1} Value {2}", deviceId, sensorId, value);
         }

         this.loggingChannel.LogEvent("Sensor readings", sensorData, LoggingLevel.Verbose);

         try
         {
            Debug.WriteLine(" CreateGroupDataAsync start");
            await this.adaFruitIOClient.CreateGroupDataAsync(this.applicationSettings.AdaFruitIOUserName, this.applicationSettings.AdaFruitIOGroupName.ToLower(), groupFeedData);
            Debug.WriteLine(" CreateGroupDataAsync finish");
         }
         catch (Exception ex)
         {
            Debug.WriteLine(" CreateGroupDataAsync failed {0}", ex.Message);
            this.loggingChannel.LogMessage("CreateGroupDataAsync failed " + ex.Message, LoggingLevel.Error);
         }
      }

      private class ApplicationSettings
      {
         [JsonProperty("AdaFruitIOBaseUrl", Required = Required.DisallowNull)]
         public string AdaFruitIOBaseUrl { get; set; }

         [JsonProperty("AdaFruitIOUserName", Required = Required.Always)]
         public string AdaFruitIOUserName { get; set; }

         [JsonProperty("AdaFruitIOApiKey", Required = Required.Always)]
         public string AdaFruitIOApiKey { get; set; }

         [JsonProperty("AdaFruitIOGroupName", Required = Required.Always)]
         public string AdaFruitIOGroupName { get; set; }

         [JsonProperty("RF24Address", Required = Required.Always)]
         public string RF24Address { get; set; }

         [JsonProperty("RF24Channel", Required = Required.Always)]
         public byte RF24Channel { get; set; }

         [JsonProperty("RF24DataRate", Required = Required.Always)]
         [JsonConverter(typeof(StringEnumConverter))]
         public DataRate RF24DataRate { get; set; }

         [JsonProperty("RF24PowerLevel", Required = Required.Always)]
         [JsonConverter(typeof(StringEnumConverter))]
         public PowerLevel RF24PowerLevel { get; set; }

         [JsonProperty("RF24AutoAcknowledge", Required = Required.Always)]
         public bool IsRF24AutoAcknowledge { get; set; }

         [JsonProperty("RF24DynamicAcknowledge", Required = Required.Always)]
         public bool IsRF24DynamicAcknowledge { get; set; }

         [JsonProperty("RF24DynamicPayload", Required = Required.Always)]
         public bool IsRF24DynamicPayload { get; set; }
      }
   }
}
