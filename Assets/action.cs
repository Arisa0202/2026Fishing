using UnityEngine;
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using System.Globalization;

public class action : MonoBehaviour
{
    [Header("Arduino シリアル通信設定")]
    public string portName = "COM3";
    public int baudRate = 115200;

    private SerialPort serial;
    private Thread receiveThread;
    private volatile bool running = false;
    private ConcurrentQueue<string> dataQueue = new ConcurrentQueue<string>();

    [Header("竿（オブジェクト）の回転設定")]
    public Transform targetObject;
    private Quaternion initialRotation;
    public float rotationMultiplier = 1.0f;
    public float smoothSpeed = 10.0f;

    [Header("Haptic Reactor 触覚出力設定")]
    public AudioSource hapticSource;
    public AudioClip castReleaseClip;
    public AudioClip underwaterLoopClip;
    public AudioClip hookHitClip;
    public AudioClip reelingLoopClip;

    [Header("デバッグ設定（Arduinoなしテスト用）")]
    public bool enableKeyboardDebug = true;

    private float accX;
    private float accY;
    private float accZ = 1.0f;
    private Quaternion targetRotation;

    private int currentState = 1;
    private float fixedStartTime = 0f;

    void Start()
    {
        if (targetObject != null)
        {
            initialRotation = targetObject.localRotation;
            targetRotation = initialRotation;
        }

        try
        {
            serial = new SerialPort(portName, baudRate);
            serial.ReadTimeout = 100;
            serial.Open();

            running = true;

            receiveThread = new Thread(ReceiveData);
            receiveThread.IsBackground = true;
            receiveThread.Start();

            Debug.Log("Arduino Connected");
            Debug.Log("状態1：自由に回転");
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("Arduino未接続（矢印キー・Spaceキーでテスト可能）：" + e.Message);
        }
    }

    void ReceiveData()
    {
        while (running)
        {
            try
            {
                string data = serial.ReadLine();
                dataQueue.Enqueue(data.Trim());
            }
            catch (System.TimeoutException) { }
            catch { break; }
        }
    }

    void Update()
    {
        while (dataQueue.TryDequeue(out string data))
        {
            ParseData(data);
        }

        if (enableKeyboardDebug)
        {
            HandleKeyboardDebug();
        }

        // 状態2以外では目標回転へ滑らかに補間
        if (currentState != 2 && targetObject != null)
        {
            targetObject.localRotation = Quaternion.Slerp(
                targetObject.localRotation,
                targetRotation,
                Time.deltaTime * smoothSpeed
            );
        }
    }

    void HandleKeyboardDebug()
    {
        // Spaceキーで状態を順番に切り替え（1 → 2 → 3 → 1）
        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (currentState == 1) EnterState2();
            else if (currentState == 2) EnterState3();
            else if (currentState == 3) EnterState1();
        }

        // 数字キーで直接切り替え
        if (Input.GetKeyDown(KeyCode.Alpha1)) EnterState1();
        if (Input.GetKeyDown(KeyCode.Alpha2)) EnterState2();
        if (Input.GetKeyDown(KeyCode.Alpha3)) EnterState3();

        // 状態1 または 状態3 のときに矢印キーで回転テスト
        if (currentState == 1 || currentState == 3)
        {
            float h = Input.GetAxis("Horizontal"); // ← → キー
            float v = Input.GetAxis("Vertical");   // ↑ ↓ キー

            if (Mathf.Abs(h) > 0.01f || Mathf.Abs(v) > 0.01f)
            {
                accX = -v;
                accY = h;
                accZ = 1.0f;
                UpdateTargetRotation();
            }
            else if (!running)
            {
                // Arduino未接続時、キーを離したら元の姿勢（水平）へ戻す
                accX = 0f;
                accY = 0f;
                accZ = 1.0f;
                UpdateTargetRotation();
            }
        }
    }

    void ParseData(string data)
    {
        if (!data.StartsWith("X:")) return;

        string[] values = data.Split(
            new char[] { ' ', '\t' },
            System.StringSplitOptions.RemoveEmptyEntries
        );

        if (values.Length < 6) return;

        float x = 0f, y = 0f, z = 0f;
        bool valid =
            float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out x) &&
            float.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y) &&
            float.TryParse(values[5], NumberStyles.Float, CultureInfo.InvariantCulture, out z);

        if (!valid) return;

        accX = x;
        accY = y;
        accZ = z;

        if (currentState == 1)
        {
            if (x < -1.0f && z < 0.4f) EnterState2();
        }
        else if (currentState == 2)
        {
            if (x > -0.5f) EnterState3();
        }
        else if (currentState == 3)
        {
            if (x <= 0.5f) EnterState1();
        }

        if (currentState != 2)
        {
            UpdateTargetRotation();
        }
    }

    void EnterState1()
    {
        currentState = 1;
        accX = 0f;
        accY = 0f;
        accZ = 1.0f;
        UpdateTargetRotation();

        StopHapticLoop();
        Debug.Log("状態1：自由に回転");
    }

    void EnterState2()
    {
        currentState = 2;
        fixedStartTime = Time.time;

        accX = -2.0f;
        accY = 0f;
        accZ = 0.2f;
        UpdateTargetRotation();
        if (targetObject != null)
        {
            targetObject.localRotation = targetRotation;
        }

        PlayHapticOneShot(castReleaseClip, 1.0f);
        PlayHapticLoop(underwaterLoopClip, 0.4f);

        Debug.Log("状態2：キャスト完了・水中で固定開始");
    }

    void EnterState3()
    {
        currentState = 3;

        PlayHapticOneShot(hookHitClip, 1.0f);
        PlayHapticLoop(reelingLoopClip, 0.8f);

        Debug.Log("状態3：つり上げ開始");
    }

    void UpdateTargetRotation()
    {
        float pitch = Mathf.Atan2(
            -accX,
            Mathf.Sqrt(accY * accY + accZ * accZ)
        ) * Mathf.Rad2Deg;

        float roll = Mathf.Atan2(
            accY,
            accZ
        ) * Mathf.Rad2Deg;

        targetRotation = initialRotation *
            Quaternion.Euler(
                pitch * rotationMultiplier,
                0,
                roll * rotationMultiplier
            );
    }

    void PlayHapticOneShot(AudioClip clip, float volume)
    {
        if (hapticSource != null && clip != null)
        {
            hapticSource.PlayOneShot(clip, volume);
        }
    }

    void PlayHapticLoop(AudioClip clip, float volume)
    {
        if (hapticSource == null) return;

        if (clip != null)
        {
            hapticSource.clip = clip;
            hapticSource.volume = volume;
            hapticSource.loop = true;
            hapticSource.Play();
        }
        else
        {
            hapticSource.Stop();
        }
    }

    void StopHapticLoop()
    {
        if (hapticSource != null && hapticSource.isPlaying)
        {
            hapticSource.Stop();
        }
    }

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