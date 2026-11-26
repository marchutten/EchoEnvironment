/*
  Read an 8x8 array of distances from the VL53L5CX
  By: Nathan Seidle
  SparkFun Electronics
  Date: October 26, 2021
  License: MIT. See license file for more information but you can
  basically do whatever you want with this code.

  This example shows how to setup the I2C bus to minimize the amount
  of time taken to init the sensor.

  At each power on reset, a staggering 86,000 bytes of firmware have to be sent to the sensor.
  At 100kHz, this can take ~9.4s. By increasing the clock speed, we can cut this time down to ~1.4s.

  Two parameters can be tweaked:

    Clock speed: The VL53L5CX has a max bus speed of 1MHz.

    Max transfer size: The majority of Arduino platforms default to 32 bytes. If you are using one
    with a larger buffer (ESP32 is 128 bytes for example), this can help decrease transfer times a bit.

  Measurements:
    Default 100kHz clock and 32 byte transfer: 9.4s
    400kHz, 32 byte transfer: 2.8s
    400kHz, 128 byte transfer: 2.5s
    1MHz, 32 byte transfer: 1.65s
    1MHz, 128 byte transfer: 1.4s

  Feel like supporting our work? Buy a board from SparkFun!
  https://www.sparkfun.com/products/18642

*/

#include <Wire.h>

#define SDA_PIN 8  // Vul jouw SDA pin in
#define SCL_PIN 9  // Vul jouw SCL pin in

#include <SparkFun_VL53L5CX_Library.h> //http://librarymanager/All#SparkFun_VL53L5CX

// I2C Adressen van de oude Drotek 10DOF
#define ITG3200_ADDR  0x69  // Gyroscoop
#define BMA180_ADDR   0x40  // Accelerometer
#define HMC5883L_ADDR 0x1E  // Magnetometer (Kompas)
#define BARO_ADDR     0x77  // BMP085 of MS5611 Barometer

int16_t readBigEndian16();


SparkFun_VL53L5CX myImager;
VL53L5CX_ResultsData measurementData; // Result data class structure, 1356 byes of RAM

int imageResolution = 0; //Used to pretty print output
int imageWidth = 0; //Used to pretty print output

void setup()
{
  Serial.begin(115200);
  delay(1000);
  Serial.println("SparkFun VL53L5CX Imager Example");

  Wire.begin(SDA_PIN, SCL_PIN);
  //Wire.begin(); //This resets I2C bus to 100kHz
  //Wire.setClock(1000000); //Sensor has max I2C freq of 1MHz
  Wire.setClock(400000); //Sensor has max I2C freq of 400KHz

  Serial.println("Initialiseren van Drotek 10DOF sensoren...");

  initITG3200();
  initBMA180();
  initHMC5883L();

  Serial.println("Initializing sensor board. This can take up to 10s. Please wait.");

  //Time how long it takes to transfer firmware to sensor
  long startTime = millis();
  bool startup = myImager.begin();
  long stopTime = millis();

  if (startup == false)
  {
    Serial.println(F("Sensor not found - check your wiring. Freezing"));
    while (1) ;
  }

  Serial.print("Firmware transfer time: ");
  float timeTaken = (stopTime - startTime) / 1000.0;
  Serial.print(timeTaken, 3);
  Serial.println("s");

  myImager.setResolution(8*8); //Enable all 64 pads

  imageResolution = myImager.getResolution(); //Query sensor for current resolution - either 4x4 or 8x8
  imageWidth = sqrt(imageResolution); //Calculate printing width
  
  myImager.setRangingFrequency(15);
  myImager.startRanging();
}

void loop()
{
  static int16_t gx = 0, gy = 0, gz = 0;
  static int16_t ax = 0, ay = 0, az = 0;
  static int16_t mx = 0, my = 0, mz = 0;

  //Poll sensor for new data
  if (myImager.isDataReady() == true)
  {
    readITG3200(&gx, &gy, &gz);
    readBMA180(&ax, &ay, &az);
    readHMC5883L(&mx, &my, &mz);

    if (myImager.getRangingData(&measurementData)) //Read distance data into array
    {
      //The ST library returns the data transposed from zone mapping shown in datasheet
      //Pretty-print data with increasing y, decreasing x to reflect reality
      for (int y = 0 ; y <= imageWidth * (imageWidth - 1) ; y += imageWidth)
      {
        for (int x = imageWidth - 1 ; x >= 0 ; x--)
        {
          Serial.print(measurementData.distance_mm[x + y]);
          Serial.print(",");
        }
      }

      Serial.print(gx); Serial.print(",");
      Serial.print(gy); Serial.print(",");
      Serial.print(gz); Serial.print(",");

      Serial.print(ax); Serial.print(",");
      Serial.print(ay); Serial.print(",");
      Serial.print(az); Serial.print(",");

      Serial.print(mx); Serial.print(",");
      Serial.print(my); Serial.print(",");
      Serial.println(mz);
    }
  }

  delay(1); //Small delay between polling
}


// --- 1. ITG-3200 (Gyroscoop op 0x69) ---
void initITG3200() {
  writeRegister(ITG3200_ADDR, 0x3E, 0x80); // Reset
  delay(50);
  writeRegister(ITG3200_ADDR, 0x16, 0x1B); // +/- 2000 deg/s, 42Hz low-pass filter
  writeRegister(ITG3200_ADDR, 0x3E, 0x03); // PLL met Z-gyro referentie
}

void readITG3200(int16_t* x, int16_t* y, int16_t* z) {
  Wire.beginTransmission(ITG3200_ADDR);
  Wire.write(0x1D); // Startregister voor X_H
  Wire.endTransmission(false);
  Wire.requestFrom(ITG3200_ADDR, 6);

  if (Wire.available() == 6) {
    *x = readBigEndian16();
    *y = readBigEndian16();
    *z = readBigEndian16();
  }
}

// --- 2. BMA180
void initBMA180() {
  writeRegister(BMA180_ADDR, 0x10, 0xB6); // Soft reset
  delay(20);
  // Schrijfbeveiliging opheffen (ee_w bit zetten in ctrl_reg0)
  uint8_t ctrl0 = readRegister(BMA180_ADDR, 0x0D);
  writeRegister(BMA180_ADDR, 0x0D, ctrl0 | 0x10);
  delay(10);

  // Bandbreedte op 20Hz zetten
  uint8_t bw = readRegister(BMA180_ADDR, 0x20);
  writeRegister(BMA180_ADDR, 0x20, (bw & 0x0F) | 0x10);

  // Bereik instellen op +/- 2g
  uint8_t range = readRegister(BMA180_ADDR, 0x35);
  writeRegister(BMA180_ADDR, 0x35, (range & 0xF1) | (0x02 << 1));
}

void readBMA180(int16_t* x, int16_t* y, int16_t* z) {
  Wire.beginTransmission(BMA180_ADDR);
  Wire.write(0x02); // Startregister ACC_X_LSB
  Wire.endTransmission(false);
  Wire.requestFrom(BMA180_ADDR, 6);

  if (Wire.available() == 6) {
    // BMA180 geeft 14-bits data (LSB zit in bits 7..2)
    uint8_t x_lsb = Wire.read();
    uint8_t x_msb = Wire.read();
    uint8_t y_lsb = Wire.read();
    uint8_t y_msb = Wire.read();
    uint8_t z_lsb = Wire.read();
    uint8_t z_msb = Wire.read();

    *x = ((int16_t)((x_msb << 8) | x_lsb)) >> 2;
    *y = ((int16_t)((y_msb << 8) | y_lsb)) >> 2;
    *z = ((int16_t)((z_msb << 8) | z_lsb)) >> 2;
  }
}

// --- 3. HMC5883L (Kompas op 0x1E) ---
void initHMC5883L() {
  writeRegister(HMC5883L_ADDR, 0x00, 0x70); // 8 samples gemiddeld, 15Hz
  writeRegister(HMC5883L_ADDR, 0x01, 0x20); // Gain 1.3 Ga (1090 LSB/Gauss)
  writeRegister(HMC5883L_ADDR, 0x02, 0x01); // Single meting starten (wordt na elke read opnieuw gestart)
  delay(10);
}

void readHMC5883L(int16_t* x, int16_t* y, int16_t* z) {
  Wire.beginTransmission(HMC5883L_ADDR);
  Wire.write(0x03); // Startregister Data Output X MSB
  Wire.endTransmission(false);
  Wire.requestFrom(HMC5883L_ADDR, 6);

  if (Wire.available() == 6) {
    // Let op: HMC5883L stuurt X, dan Z, dan Y!
    *x = readBigEndian16();
    *z = readBigEndian16();
    *y = readBigEndian16();
  }

  // Volgende meting starten. Single-measurement mode garandeert verse data bij elke read
  // (continue mode leverde op dit board steeds dezelfde, bevroren waarden op). Een meting
  // duurt ~6 ms; de VL53L5CX levert op 15 Hz, dus ruim op tijd klaar voor de volgende loop.
  writeRegister(HMC5883L_ADDR, 0x02, 0x01);
}

// Leest MSB en dan LSB in een gegarandeerde volgorde. '(Wire.read() << 8) | Wire.read()' is
// FOUT: C++ legt de evaluatievolgorde van de twee operanden niet vast, dus de compiler mag de
// LSB eerst lezen en de bytes verwisselen. Dat geeft onzinwaarden en verkeerde bereiken.
int16_t readBigEndian16() {
  uint8_t msb = Wire.read();
  uint8_t lsb = Wire.read();
  return (int16_t)((msb << 8) | lsb);
}

// --- Hulpfuncties voor I2C ---
void writeRegister(uint8_t devAddr, uint8_t regAddr, uint8_t val) {
  Wire.beginTransmission(devAddr);
  Wire.write(regAddr);
  Wire.write(val);
  Wire.endTransmission();
}

uint8_t readRegister(uint8_t devAddr, uint8_t regAddr) {
  Wire.beginTransmission(devAddr);
  Wire.write(regAddr);
  Wire.endTransmission(false);
  Wire.requestFrom(devAddr, (uint8_t)1);
  return Wire.available() ? Wire.read() : 0;
}
