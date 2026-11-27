#include <Wire.h>

#define MPU_ADDR 0x68

//MPU6050	ESP32-S3
//VCC	3.3V
//GND	GND
//SDA	GPIO21
//SCL	GPIO19

// MPU-6500のレジスタに1バイト書き込む
void writeRegister(byte reg, byte data) {
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(reg);
  Wire.write(data);
  Wire.endTransmission();
}

// 2バイトのデータを読み取る
int16_t read16(byte reg) {
  Wire.beginTransmission(MPU_ADDR);
  Wire.write(reg);
  Wire.endTransmission(false);

  Wire.requestFrom(MPU_ADDR, (uint8_t)2);

  if (Wire.available() < 2) return 0;

  int16_t value = Wire.read() << 8;
  value |= Wire.read();

  return value;
}

void setup() {
  Serial0.begin(115200);
  delay(1500);

  Wire.begin(21, 19);
  Wire.setClock(50000);

  Serial0.println("MPU6500 Start");

  // スリープ解除
  writeRegister(0x6B, 0x00);
  delay(100);

  // 加速度の測定範囲を±2gに設定
  writeRegister(0x1C, 0x00);

  Serial0.println("Sensor Ready");
}

void loop() {
  int16_t ax = read16(0x3B);
  int16_t ay = read16(0x3D);
  int16_t az = read16(0x3F);

  // ±2g設定では16384が1g
  float x = ax / 16384.0;
  float y = ay / 16384.0;
  float z = az / 16384.0;

  Serial0.print("X: ");
  Serial0.print(x, 2);

  Serial0.print("  Y: ");
  Serial0.print(y, 2);

  Serial0.print("  Z: ");
  Serial0.println(z, 2);

  delay(100);
}