
namespace Hackathon2021.WTFarmbeats
{
	using System;
	using System.Device.I2c;
	using System.Globalization;
	using System.IO;
	using System.Net;
	using System.Text;
	using System.Threading;
	using System.Threading.Tasks;

	using Azure.Storage.Blobs.Models;
	using Azure.Storage.Blobs.Specialized;
	using Microsoft.Extensions.Configuration;
	using Microsoft.Azure.Devices.Client;
	using Microsoft.Azure.Devices.Client.Transport;

	using Newtonsoft.Json;
	using Newtonsoft.Json.Linq;

	using devMobile.IoT.NetCore.GroveBaseHat;
	using devMobile.IoT.NetCore.Sensirion;

	class Program
	{
		private static bool CameraBusy = false;
		private static DeviceClient AzureIoTCentralClient;
		private static AnalogPorts GrovePiHatAnalogPorts = null;
		private static Sht20 Sht20Sensor = null;
		private static ApplicationConfig applicationConfig = null;

		static async Task Main()
		{
			CancellationToken stoppingToken = new CancellationToken();
			Timer imageUpdatetimer;
			Timer sensorUpdatetimer;
			int raspberryPiBusId = 1;

			Console.WriteLine("Hackathon2021.WTFarmbeats PoC client");

			IConfiguration configuration = new ConfigurationBuilder()
				 .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
				 .Build();

			applicationConfig = configuration.Get<ApplicationConfig>();

			Console.WriteLine($"Camera:{applicationConfig.CameraUrl} UserName:{applicationConfig.UserName} Image upload period {applicationConfig.ImageUploadInterval} minutes ");

			I2cDevice i2cDevice1 = I2cDevice.Create(new(raspberryPiBusId, AnalogPorts.DefaultI2cAddress));
			GrovePiHatAnalogPorts = new AnalogPorts(i2cDevice1);

			I2cDevice i2cDevice2 = I2cDevice.Create(new(raspberryPiBusId, Sht20.DefaultI2cAddress));
			Sht20Sensor = new Sht20(i2cDevice2);

			#region AzureIoT Hub connection string creation
			try
			{
				AzureIoTCentralClient = DeviceClient.CreateFromConnectionString(applicationConfig.AzureIoTHubConnectionString);

				await AzureIoTCentralClient.OpenAsync();
			}
			catch (Exception ex)
			{
				Console.WriteLine($"AzureIOT Hub DeviceClient.CreateFromConnectionString failed {ex.Message}");
				return;
			}
			#endregion

			imageUpdatetimer = new Timer(ImageUpdateTimerCallback, null, new TimeSpan(0, applicationConfig.ImageUploadInterval, 0), new TimeSpan(0, applicationConfig.ImageUploadInterval, 0));

			sensorUpdatetimer = new Timer(SensorUpdateTimerCallback, null, new TimeSpan(0, applicationConfig.SensorUploadInterval, 0), new TimeSpan(0, applicationConfig.SensorUploadInterval, 0));

			try
			{
				await Task.Delay(Timeout.Infinite, stoppingToken);
			}
			catch (TaskCanceledException)
			{
				Console.WriteLine("devMobile.IoT.SecurityCameraClient.AzureIoTHubImageUploadTimer stopping");
			}
			finally
			{
				AzureIoTCentralClient?.Dispose();

				Sht20Sensor?.Dispose();
				i2cDevice2?.Dispose();

				GrovePiHatAnalogPorts?.Dispose();
				i2cDevice1?.Dispose();

			}

			Console.WriteLine("Press <enter> to exit");
			Console.ReadLine();
		}

		private static async void ImageUpdateTimerCallback(object state)
		{
			DateTime requestAtUtc = DateTime.UtcNow;

			// Just incase - stop code being called while retrivel of the photo already in progress
			if (CameraBusy)
			{
				return;
			}
			CameraBusy = true;

			Console.WriteLine($"{requestAtUtc:yy-MM-dd HH:mm:ss} Image up load start");

			try
			{
				// First go and get the image file from the camera onto local file system
				using (var client = new WebClient())
				{
					NetworkCredential networkCredential = new NetworkCredential()
					{
						UserName = applicationConfig.UserName,
						Password = applicationConfig.Password
					};

					client.Credentials = networkCredential;

					await client.DownloadFileTaskAsync(new Uri(applicationConfig.CameraUrl), applicationConfig.LocalFilename);
				}

				// Then open the file ready to stream ito upto storage account associated with Azuure IoT Hub
				using (FileStream fileStreamSource = new FileStream(applicationConfig.LocalFilename, FileMode.Open))
				{
					var fileUploadSasUriRequest = new FileUploadSasUriRequest
					{
						BlobName = string.Format("{0:yyMMdd}/{0:yyMMddHHmmss}.jpg", requestAtUtc)
					};

					// Get the plumbing sorted for where the file is going in Azure Storage
					FileUploadSasUriResponse sasUri = await AzureIoTCentralClient.GetFileUploadSasUriAsync(fileUploadSasUriRequest);
					Uri uploadUri = sasUri.GetBlobUri();

					try
					{
						var blockBlobClient = new BlockBlobClient(uploadUri);

						var response = await blockBlobClient.UploadAsync(fileStreamSource, new BlobUploadOptions());

						var successfulFileUploadCompletionNotification = new FileUploadCompletionNotification()
						{
							// Mandatory. Must be the same value as the correlation id returned in the sas uri response
							CorrelationId = sasUri.CorrelationId,

							// Mandatory. Will be present when service client receives this file upload notification
							IsSuccess = true,

							// Optional, user defined status code. Will be present when service client receives this file upload notification
							StatusCode = 200,

							// Optional, user-defined status description. Will be present when service client receives this file upload notification
							StatusDescription = "Success"
						};

						await AzureIoTCentralClient.CompleteFileUploadAsync(successfulFileUploadCompletionNotification);
					}
					catch (Exception ex)
					{
						Console.WriteLine($"Failed to upload file to Azure Storage using the Azure Storage SDK due to {ex}");

						var failedFileUploadCompletionNotification = new FileUploadCompletionNotification
						{
							// Mandatory. Must be the same value as the correlation id returned in the sas uri response
							CorrelationId = sasUri.CorrelationId,

							// Mandatory. Will be present when service client receives this file upload notification
							IsSuccess = false,

							// Optional, user-defined status code. Will be present when service client receives this file upload notification
							StatusCode = 500,

							// Optional, user defined status description. Will be present when service client receives this file upload notification
							StatusDescription = ex.Message
						};

						await AzureIoTCentralClient.CompleteFileUploadAsync(failedFileUploadCompletionNotification);
					}
				}

				TimeSpan uploadDuration = DateTime.UtcNow - requestAtUtc;

				Console.WriteLine($"{DateTime.UtcNow:yy-MM-dd HH:mm:ss} Image up load done. Duration:{uploadDuration.TotalMilliseconds:0.} mSec");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Camera image upload process failed {ex.Message}");
			}
			finally
			{
				CameraBusy = false;
			}
		}

		private static async void SensorUpdateTimerCallback(object state)
		{
			DateTime requestAtUtc = DateTime.UtcNow;

			double soilMoistureOutdoor1 = GrovePiHatAnalogPorts.Read(AnalogPorts.AnalogPort.A0);
			soilMoistureOutdoor1 = Map(soilMoistureOutdoor1, 0.18, 84.0, 0.0, 100);

			double soilMoistureOutdoor2 = GrovePiHatAnalogPorts.Read(AnalogPorts.AnalogPort.A2);
			soilMoistureOutdoor2 = Map(soilMoistureOutdoor2, 0.2, 84.0, 0.0, 100);

			double lightLevel = GrovePiHatAnalogPorts.Read(AnalogPorts.AnalogPort.A6);

			double temperature = Sht20Sensor.Temperature();
			double humidity = Sht20Sensor.Humidity();

			Console.WriteLine($"{requestAtUtc:yy-MM-dd HH:mm:ss} Temperature:{temperature:F1}°C Humidity:{humidity:F0}% Outdoor1:{soilMoistureOutdoor1:F1} Outdoor2:{soilMoistureOutdoor2:F1} Light:{lightLevel:F1}");

			JObject telemetryMessage = new JObject
			{
				{ "SoilMoistureOutdoor1", Math.Round(soilMoistureOutdoor1, 1) },
				{ "SoilMoistureOutdoor2", Math.Round(soilMoistureOutdoor2, 1) },
				{ "Light", Math.Round(lightLevel, 1) },
				{ "Temperature", Math.Round(temperature, 1) },
				{ "Humidity", Math.Round(humidity, 1) }
			};

			using (Message ioTHubmessage = new Message(Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(telemetryMessage))))
			{
				// Ensure the displayed time is the acquired time rather than the uploaded time. 
				ioTHubmessage.Properties.Add("iothub-creation-time-utc", DateTime.UtcNow.ToString("s", CultureInfo.InvariantCulture));

				await AzureIoTCentralClient.SendEventAsync(ioTHubmessage);
			}
		}

		public static double Map(double value, double fromSource, double toSource, double fromTarget, double toTarget)
		{
			return (value - fromSource) / (toSource - fromSource) * (toTarget - fromTarget) + fromTarget;
		}
	}

	public class ApplicationConfig
	{
		public string CameraUrl { get; set; }

		public string UserName { get; set; }

		public string Password { get; set; }

		public string LocalFilename { get; set; }

		public string AzureIoTHubConnectionString { get; set; }

		public ushort ImageUploadInterval { get; set; }

		public ushort SensorUploadInterval { get; set; }
	}
}


/*
	Sample JOSN on appsettings gile
	{
		"CameraUrl": "http://10.0.0.50:85/images/snapshot.jpg",
		"UserName": "BobTheBuilder",
		"Password": "!@#$%^&*()",
		"LocalFilename": "LatestImage.jpg",
		"AzureIoTHubConnectionString": "HostName=....",
		"ImageUploadInterval": 5,
		"SensorUploadInterval": 1
}
*/