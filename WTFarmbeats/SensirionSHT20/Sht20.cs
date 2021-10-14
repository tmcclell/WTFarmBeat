//---------------------------------------------------------------------------------
// Copyright (c) September 2021, devMobile Software
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
// Inspired by Arduino library https://github.com/RobTillaart/SHT2x thankyou Rob Tillart
//
//---------------------------------------------------------------------------------
namespace devMobile.IoT.NetCore.Sensirion
{
	using System;
	using System.Device.I2c;
	using System.Threading;

	public class Sht20 : IDisposable
	{
		public const int DefaultI2cAddress = 0x40;
		private const byte UserRegisterRead = 0xE7;
		private const byte UserRegisterWrite = 0xE6; // need this for heater and reset
		private const byte TemperatureNoHold = 0xF3;
		private const byte HumidityNoHold = 0xf5;

		private I2cDevice _i2cDevice = null;

		public Sht20(I2cDevice i2cDevice)
		{
			byte[] writeBuffer = new byte[1] { UserRegisterRead };
			byte[] readBuffer = new byte[1] { 0 };
			_i2cDevice = i2cDevice ?? throw new ArgumentNullException(nameof(i2cDevice));

			_i2cDevice.WriteRead(writeBuffer, readBuffer);

			if (readBuffer[0] == 0)
			{
				throw new ApplicationException("GroveBaseHatRPI not found");
			}
		}


		public double Temperature()
		{
			byte[] readBuffer = new byte[3] { 0, 0, 0 };
			_i2cDevice = _i2cDevice ?? throw new ArgumentNullException(nameof(_i2cDevice));

			_i2cDevice.WriteByte(TemperatureNoHold);

			Thread.Sleep(70);

			_i2cDevice.Read(readBuffer);

			ushort rawTemperature = (ushort)(readBuffer[0] << 8);
			rawTemperature += readBuffer[1];

			double temperature = rawTemperature * (175.72 / 65536.0) - 46.85; // 0.0026812744140625

			return temperature;
		}

		public double Humidity()
		{
			byte[] readBuffer = new byte[3] { 0, 0, 0 };
			_i2cDevice = _i2cDevice ?? throw new ArgumentNullException(nameof(_i2cDevice));

			_i2cDevice.WriteByte(HumidityNoHold);

			Thread.Sleep(70);

			_i2cDevice.Read(readBuffer);

			ushort rawTemperature = (ushort)(readBuffer[0] << 8);
			rawTemperature += readBuffer[1];


			double humidity = rawTemperature * (125.0 / 65536.0) - 6.0;

			return humidity;
		}

		public bool Reset()
		{
			/*
  bool b = writeCmd(SHT2x_SOFT_RESET);
  if (b == false) return false;
  return true;
			*/
			return true;
		}

		public void HeaterOn()
		{

		}

		public bool IsHeaterOn()
		{
			return false;
		}

		public void HeaterOff()
		{

		}


		public void Dispose()
		{
			_i2cDevice?.Dispose();
			_i2cDevice = null!;
		}
	}
}

