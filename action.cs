
using UnityEngine;
using System.IO.Ports;
using System.Threading;
using System.Collections.Concurrent;
using System.Globalization;

public class action : MonoBehaviour
{
    // Arduinoとのシリアル通信
    private SerialPort serial;

    // 受信スレッド
    private Thread receiveThread;
    private volatile bool running = false;

    // 受信データを保存するキュー
    private ConcurrentQueue<string> dataQueue =
        new ConcurrentQueue<string>();

    // Unityで傾ける物体
    public Transform targetObject;

    // 初期状態の回転
    private Quaternion initialRotation;

    // 傾きの調整倍率
    public float rotationMultiplier = 1.0f;

    // 回転の滑らかさ
    public float smoothSpeed = 10.0f;

    // 加速度データ
    private float accX;
    private float accY;
    private float accZ;

    // 目標回転
    private Quaternion targetRotation;

    // 状態管理
    // 1：自由に回転
    // 2：水中で固定
    // 3：つり上げ
    private int currentState = 1;

    // x < -1 が続いた時間
    private float sinkingStartTime = 0f;

    // 状態2へ移行するまでの時間
    private float sinkingDuration = 2.0f;

    // 状態2の固定開始時刻
    private float fixedStartTime = 0f;

    // 固定時間
    private float fixedDuration = 2.0f;

    void Start()
    {
        if (targetObject != null)
        {
            initialRotation = targetObject.localRotation;
            targetRotation = initialRotation;
        }

        try
        {
            serial = new SerialPort("COM6", 115200);
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
            Debug.LogError("Arduino接続エラー：" + e.Message);
        }
    }

    // Arduinoからデータを受信
    void ReceiveData()
    {
        while (running)
        {
            try
            {
                string data = serial.ReadLine();
                dataQueue.Enqueue(data.Trim());
            }
            catch (System.TimeoutException)
            {
                // タイムアウトは無視
            }
            catch
            {
                break;
            }
        }
    }

    // Unityのメインスレッドで処理
    void Update()
    {
        while (dataQueue.TryDequeue(out string data))
        {
            ParseData(data);
        }

        // 状態2以外では回転を反映
        if (currentState != 2 && targetObject != null)
        {
            targetObject.localRotation = Quaternion.Slerp(
                targetObject.localRotation,
                targetRotation,
                Time.deltaTime * smoothSpeed
            );
        }
    }

    // 加速度データの解析
    void ParseData(string data)
    {
        if (!data.StartsWith("X:"))
            return;

        string[] values = data.Split(
            new char[] { ' ', '\t' },
            System.StringSplitOptions.RemoveEmptyEntries
        );

        if (values.Length < 6)
            return;

        float x = 0f;
        float y = 0f;
        float z = 0f;

    bool valid =
        float.TryParse(values[1], NumberStyles.Float,
            CultureInfo.InvariantCulture, out x) &&
        float.TryParse(values[3], NumberStyles.Float,
            CultureInfo.InvariantCulture, out y) &&
        float.TryParse(values[5], NumberStyles.Float,
            CultureInfo.InvariantCulture, out z);

if (!valid)
    return;

        accX = x;
        accY = y;
        accZ = z;

        // ----------------------------
        // 状態1：自由に回転
        // ----------------------------
        if (currentState == 1)
        {
        // 一瞬でもx < -1,z<0.4になったら状態2へ
            if (x < -1.0f && z < 0.4f )
            {
                currentState = 2;

            // 固定開始時刻を記録
            fixedStartTime = Time.time;

            // xを-2に固定
            x = -2.0f;
            accX = -2.0f;
            // zを0.2に固定
            z = 0.2f;
            accZ = 0.2f;

            Debug.Log("状態2：水中で固定開始");
            }
        }

        // ----------------------------
        // 状態2：水中で固定
        // ----------------------------
        else if (currentState == 2)
        {
            // x > -0.5 になったら状態3へ
            if (x > -0.5f)
            {
                currentState = 3;

                Debug.Log("状態3：つり上げ開始");
            }
        }

        // ----------------------------
        // 状態3：つり上げ
        // ----------------------------
        else if (currentState == 3)
        {
            // x <= 0.5 になったら状態1へ
            if (x <= 0.5f)
            {
                currentState = 1;

                Debug.Log("状態1：自由に回転");
            }
        }

        // ----------------------------
        // 回転角度の計算
        // ----------------------------

        // 状態2では目標回転を更新しない
        if (currentState != 2)
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

        Debug.Log(
            "状態:" + currentState +
            " X:" + accX.ToString("F2") +
            " Y:" + accY.ToString("F2") +
            " Z:" + accZ.ToString("F2")
        );
    }

    // 終了時に通信を停止
    void OnDestroy()
    {
        running = false;

        if (receiveThread != null && receiveThread.IsAlive)
        {
            receiveThread.Join(500);
        }

        if (serial != null)
        {
            try
            {
                if (serial.IsOpen)
                    serial.Close();

                serial.Dispose();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(e.Message);
            }
        }

        Debug.Log("Arduino Disconnected");
    }
}
