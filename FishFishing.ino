#define MPU_ADDR 0x68

const int HAPTIC_PIN = 4;
const int FREQ = 160;
const int RESOLUTION = 8;

// 振動を出す関数
void vibrate(int power, int duration) {
  ledcWrite(HAPTIC_PIN, power);
  delay(duration);
  ledcWrite(HAPTIC_PIN, 0);
}

void setup() {
  ledcAttach(HAPTIC_PIN, FREQ, RESOLUTION);
}

void loop() {

  // ① 魚が餌をつつく（弱い振動2回）
  vibrate(90, 80);
  delay(120);

  vibrate(90, 80);
  delay(500);

  // ② 魚が食いつく（強い振動1回）
  vibrate(255, 250);
  delay(300);

  // ③ 魚が引っ張る（強めの振動2回）
  vibrate(180, 180);
  delay(150);

  vibrate(180, 300);

  // ④ 次の魚が来るまで待つ
  delay(2000);
}