using UnityEngine;
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using System.Globalization;

public class action : MonoBehaviour
{
    [Header("Arduino シリアル通信設定")]
    public string portName = "COM6";         // Arduinoが接続されているCOMポート名
    public int baudRate = 115200;            // 通信速度

    private SerialPort serial;               // シリアル通信を制御するオブジェクト
    private Thread receiveThread;            // データ受信をバックグラウンドで行うスレッド
    private volatile bool running = false;   // 受信スレッドを動かし続けるかどうかのフラグ
    // 受信スレッドからメインスレッドへ安全にデータを渡すためのキュー
    private ConcurrentQueue<string> dataQueue = new ConcurrentQueue<string>();

    [Header("竿（オブジェクト）の回転設定")]
    public Transform targetObject;           // 傾きを連動させる3Dの釣り竿オブジェクト
    private Quaternion initialRotation;      // 起動時の竿の初期角度
    public float rotationMultiplier = 1.0f;  // 傾きの倍率（1.0で実物と同じ角度）
    public float smoothSpeed = 10.0f;        // 回転の滑らかさ

    [Header("釣りサイクル・ランダム待機設定")]
    public float minWaitBiteTime = 2.0f;     // 魚がかかるまでの最短待機時間
    public float maxWaitBiteTime = 7.0f;     // 魚がかかるまでの最長待機時間
    public float pullUpThreshold = 0.8f;     // 釣り上げ完了とみなすX軸の値

    [Header("Haptic Reactor 触覚出力設定")]
    public AudioSource hapticSource;         // 振動用音声を再生するAudioSource
    public AudioClip castReleaseClip;        // キャスト時の単発振動データ
    public AudioClip underwaterLoopClip;     // 水中で待機中の弱いループ振動データ
    public AudioClip hookHitClip;            // 魚が食いついた瞬間の強い単発振動データ
    public AudioClip reelingLoopClip;        // 魚を引き上げている最中のループ振動データ

    [Header("デバッグ設定（Arduinoなしテスト用）")]
    public bool enableKeyboardDebug = true;  // キーボードでの操作を許可するか

    private float accX;                      // センサーから受け取ったX軸の加速度
    private float accY;                      // センサーから受け取ったY軸の加速度
    private float accZ = 1.0f;               // センサーから受け取ったZ軸の加速度）
    private Quaternion targetRotation;       // 竿が向かうべき目標の回転角度

    private int currentState = 1;            // 現在の状態（1:待機・構え, 2:水中待機, 3:つり上げ中）
    private float fixedStartTime = 0f;       // 状態2（水中待機）に入った時刻
    private float currentTargetWaitTime = 0f;// 今回ランダムに決定したヒットまでの待ち時間
    private bool isWaitingBite = false;      // 魚のアタリを待っている最中かどうかのフラグ

    // ゲーム開始時に1回だけ呼ばれる初期化処理
    void Start()
    {
        // 竿の初期角度を保存
        if (targetObject != null)
        {
            initialRotation = targetObject.localRotation;
            targetRotation = initialRotation;
        }

        // AudioSourceが未設定なら同じオブジェクトから取得（なければ自動追加）
        if (hapticSource == null)
        {
            hapticSource = GetComponent<AudioSource>();
            if (hapticSource == null)
            {
                hapticSource = gameObject.AddComponent<AudioSource>();
            }
        }
        hapticSource.playOnAwake = false; // 起動時に勝手に音が鳴らないようにする

        // 外部の音声ファイルが未割り当ての場合、低音振動波形を自動生成する
        SetupDefaultHapticClips();

        // Arduinoとのシリアル通信を開始
        try
        {
            serial = new SerialPort(portName, baudRate);
            serial.ReadTimeout = 100; // 読み取りタイムアウト（ミリ秒）
            serial.Open();

            running = true;

            // 通信待ちで画面が固まらないよう、別スレッドでデータ受信を開始
            receiveThread = new Thread(ReceiveData);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            Debug.Log("Arduino Connected");
            Debug.Log("状態1：自由に回転（キャスト待ち）");
        }
        catch (System.Exception e)
        {
            // Arduinoが繋がっていない場合は警告を出してキーボードモードで動作させる
            Debug.LogWarning("Arduino未接続（矢印キー・Spaceキーでテスト可能）：" + e.Message);
        }
    }

    // 裏スレッドでArduinoから送られてくる1行データをひたすら受信する処理
    void ReceiveData()
    {
        while (running)
        {
            try
            {
                string data = serial.ReadLine();   // 改行まで1行読み込む
                dataQueue.Enqueue(data.Trim());    // 前後の空白を消してキューに入れる
            }
            catch (System.TimeoutException) { }    // データが来ていないだけなら無視して継続
            catch { break; }                       // 切断などのエラー時はループを抜ける
        }
    }

    // 毎フレーム呼ばれるメイン更新処理
    void Update()
    {
        // キューに溜まっているArduinoからのデータをすべて取り出して解析する
        while (dataQueue.TryDequeue(out string data))
        {
            ParseData(data);
        }

        // 状態2（水中待機）のとき、ランダムに決まった待ち時間が経過したかチェック
        if (currentState == 2 && isWaitingBite)
        {
            if (Time.time - fixedStartTime >= currentTargetWaitTime)
            {
                isWaitingBite = false;
                EnterState3(); // 時間が来たら魚がヒット（状態3へ移行）
            }
        }

        // キーボードデバッグが有効ならキー入力をチェック
        if (enableKeyboardDebug)
        {
            HandleKeyboardDebug();
        }

        // 状態2（固定中）以外のときは、現在の角度から目標角度へ滑らかに回転させる
        if (currentState != 2 && targetObject != null)
        {
            targetObject.localRotation = Quaternion.Slerp(
                targetObject.localRotation,
                targetRotation,
                Time.deltaTime * smoothSpeed
            );
        }
    }

    // Arduinoなしでも動作確認するためのキーボード操作処理
    void HandleKeyboardDebug()
    {
        // Spaceキーで状態を順番に進める（1 → 2 → 3 → 1）
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (currentState == 1) EnterState2();
            else if (currentState == 2) EnterState3();
            else if (currentState == 3) EnterState1();
        }

        // 数字キー（1, 2, 3）で直接その状態へ切り替える
        if (Input.GetKeyDown(KeyCode.Alpha1)) EnterState1();
        if (Input.GetKeyDown(KeyCode.Alpha2)) EnterState2();
        if (Input.GetKeyDown(KeyCode.Alpha3)) EnterState3();

        // 状態1または状態3のとき、矢印キーで擬似的に竿を傾ける
        if (currentState == 1 || currentState == 3)
        {
            float h = Input.GetAxis("Horizontal"); // 左右矢印キー
            float v = Input.GetAxis("Vertical");   // 上下矢印キー

            if (Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f)
            {
                accX = -v;
                accY = h;
                accZ = 1.0f;
                UpdateTargetRotation();
            }
            else if (!running)
            {
                // Arduino未接続時、キーを離したら水平（初期姿勢）に戻す
                accX = 0f;
                accY = 0f;
                accZ = 1.0f;
                UpdateTargetRotation();
            }
        }
    }

    // Arduinoから届いた文字列（"X: 0.12 Y: -0.05 Z: 0.98"）を数値に変換し、状態遷移を判定する
    void ParseData(string data)
    {
        // "X:" で始まらない不要な行は無視する
        if (!data.StartsWith("X:")) return;

        // スペースやタブで文字列を分割する（[0]="X:", [1]=X値, [2]="Y:", [3]=Y値, [4]="Z:", [5]=Z値）
        string[] values = data.Split(
            new char[] { ' ', '\t' },
            System.StringSplitOptions.RemoveEmptyEntries
        );

        if (values.Length < 6) return;

        // 文字列を小数（float）に変換する
        float x = 0f, y = 0f, z = 0f;
        bool valid =
            float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
            float.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
            float.TryParse(values[5], NumberStyles.Float, CultureInfo.InvariantCulture, out z);

        if (!valid) return;

        accX = x;
        accY = y;
        accZ = z;

        // 現在の状態に応じたアクション判定
        if (currentState == 1)
        {
            // 状態1：竿を前に振り下ろしたらキャスト完了とみなして状態2へ
            if (x < -1.0f && z < 0.4f)
            {
                EnterState2();
            }
        }
        else if (currentState == 3)
        {
            // 状態3：魚がかかった後、竿をしっかり上に引き上げたら釣り上げ完了（状態1へ戻る）
            if (x > pullUpThreshold)
            {
                Debug.Log("釣り上げ完了！");
                EnterState1();
            }
        }

        // 状態2（固定中）でなければ、最新の加速度から目標角度を更新する
        if (currentState != 2)
        {
            UpdateTargetRotation();
        }
    }

    // 状態1（キャスト待ち・自由回転）へ移行する処理
    void EnterState1()
    {
        currentState = 1;
        isWaitingBite = false;
        accX = 0f;
        accY = 0f;
        accZ = 1.0f;
        UpdateTargetRotation();

        StopHapticLoop(); // すべての振動を止める
        Debug.Log("状態1：自由に回転（キャスト待ち）");
    }

    // 状態2（キャスト完了・水中待機）へ移行する処理
    void EnterState2()
    {
        currentState = 2;
        fixedStartTime = Time.time;
        isWaitingBite = true;

        // 最短〜最長の間で今回の待ち時間をランダムに決定
        currentTargetWaitTime = Random.Range(minWaitBiteTime, maxWaitBiteTime);

        // 投げ込んだ前傾姿勢で竿の角度を固定する
        accX = -2.0f;
        accY = 0f;
        accZ = 0.2f;
        UpdateTargetRotation();
        if (targetObject != null)
        {
            targetObject.localRotation = targetRotation;
        }

        Debug.Log($"状態2：キャスト完了・水中でアタリ待ち（約 {currentTargetWaitTime:F1} 秒後にヒット予定）");
        PlayHapticOneShot(castReleaseClip, 1.0f, "キャスト時の単発振動");
        PlayHapticLoop(underwaterLoopClip, 0.4f, "水中の弱いループ振動");
    }

    // 状態3（魚がヒット・つり上げ中）へ移行する処理
    void EnterState3()
    {
        currentState = 3;
        isWaitingBite = false;

        Debug.Log("状態3：魚がヒット！（つり上げ開始）");
        PlayHapticOneShot(hookHitClip, 1.0f, "【振動発生】魚が食いついた強い単発振動！");
        PlayHapticLoop(reelingLoopClip, 0.8f, "【振動発生】つり上げ中のループ振動");
    }

    // 加速度（X, Y, Z）から前後（Pitch）・左右（Roll）の傾き角度を計算して目標回転を作る
    void UpdateTargetRotation()
    {
        // 前後の傾き角度を計算（ラジアンから度数法へ変換）
        float pitch = Mathf.Atan2(
            -accX,
            Mathf.Sqrt(accY * accY + accZ * accZ)
        ) * Mathf.Rad2Deg;

        // 左右の傾き角度を計算
        float roll = Mathf.Atan2(
            accY,
            accZ
        ) * Mathf.Rad2Deg;

        // 初期角度を基準に計算した傾きを掛け合わせる
        targetRotation = initialRotation *
            Quaternion.Euler(
                pitch * rotationMultiplier,
                0,
                roll * rotationMultiplier
            );
    }

    // 未割り当ての振動クリップに対して、低音サイン波のAudioClipを自動生成する
    void SetupDefaultHapticClips()
    {
        if (castReleaseClip == null)
            castReleaseClip = CreateVibrationClip("CastRelease", 0.3f, 110f, 60f, true);

        if (underwaterLoopClip == null)
            underwaterLoopClip = CreateVibrationClip("UnderwaterLoop", 1.0f, 55f, 55f, false);

        if (hookHitClip == null)
            hookHitClip = CreateVibrationClip("HookHit", 0.4f, 85f, 45f, true);

        if (reelingLoopClip == null)
            reelingLoopClip = CreateVibrationClip("ReelingLoop", 0.5f, 95f, 75f, false);
    }

    // 指定した長さ・周波数変化・減衰設定で振動用の音声波形（サイン波）を生成する
    AudioClip CreateVibrationClip(string clipName, float duration, float startFreq, float endFreq, bool fadeOut)
    {
        int sampleRate = 44100;
        int sampleCount = Mathf.RoundToInt(sampleRate * duration);
        float[] samples = new float[sampleCount];
        float phase = 0f;

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleCount;                     // 進行度（0.0 〜 1.0）
            float freq = Mathf.Lerp(startFreq, endFreq, t);       // 開始周波数から終了周波数へ滑らかに変化
            phase += (2f * Mathf.PI * freq) / sampleRate;         // 位相を進める

            float wave = Mathf.Sin(phase);                        // サイン波を生成
            if (fadeOut) wave *= (1f - t);                        // 単発衝撃の場合は徐々に弱くする
            samples[i] = wave;
        }

        // メモリ上にAudioClipを作成して波形データをセットする
        AudioClip clip = AudioClip.Create(clipName, sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    // 1回きりの単発振動を再生し、Consoleにログを出す
    void PlayHapticOneShot(AudioClip clip, float volume, string vibrationLabel)
    {
        if (hapticSource != null && clip != null)
        {
            hapticSource.PlayOneShot(clip, volume);
            Debug.Log($"[Haptic再生] {vibrationLabel}（クリップ: {clip.name}, 音量: {volume}）");
        }
        else
        {
            Debug.Log($"[Haptic通知] {vibrationLabel}（※AudioSourceまたはClip未設定のため音は未再生）");
        }
    }

    // 連続するループ振動を再生し、Consoleにログを出す
    void PlayHapticLoop(AudioClip clip, float volume, string vibrationLabel)
    {
        if (hapticSource != null && clip != null)
        {
            hapticSource.clip = clip;
            hapticSource.volume = volume;
            hapticSource.loop = true;
            hapticSource.Play();
            Debug.Log($"[Hapticループ開始] {vibrationLabel}（クリップ: {clip.name}, 音量: {volume}）");
        }
        else
        {
            if (hapticSource != null) hapticSource.Stop();
            Debug.Log($"[Hapticループ通知] {vibrationLabel}（※AudioSourceまたはClip未設定のため音は未再生）");
        }
    }

    // 再生中のループ振動を停止する
    void StopHapticLoop()
    {
        if (hapticSource != null && hapticSource.isPlaying)
        {
            hapticSource.Stop();
        }
        Debug.Log("[Haptic停止] すべての振動を停止しました");
    }

    // ゲーム終了時やオブジェクト破棄時に、通信スレッドとCOMポートを安全に閉じる
    void OnDestroy()
    {
        running = false;
        if (receiveThread != null && receiveThread.IsAlive) receiveThread.Join(500);
        if (serial != null)
        {
            try
            {
                if (serial.IsOpen) serial.Close();
                serial.Dispose();
            }
            catch (System.Exception e) { Debug.LogWarning(e.Message); }
        }
        Debug.Log("Arduino Disconnected");
    }
}