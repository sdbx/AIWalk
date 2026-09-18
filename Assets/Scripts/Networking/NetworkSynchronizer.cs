using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AIWalk.Networking;
using UnityEngine;

[Serializable]
public enum SyncMode
{
  OnlyHostSend,
  SendOnly,
  ReceiveOnly,
}


[RequireComponent(typeof(NetworkIdentity))]
public class NetworkSynchronizer : NetBehaviour
{
    [SerializeField]
    private SyncMode syncMode = SyncMode.OnlyHostSend;
    [SerializeField]
    private List<VariableReference> variables = new();

    [SerializeField]
    [Range(1, 60)]
    private int networkUpdateFps = 20;

    private float timer;

    // 수신할 때 매번 List.Find 하지 않도록 캐시
    private readonly Dictionary<string, VariableReference> variableMap = new();
    

    protected override void OnNetworkReady()
    {
        BuildVariableMap();
        
        if ((syncMode == SyncMode.OnlyHostSend&&IsHost) || syncMode == SyncMode.SendOnly)
        {
            SubscribeGlobalEvent("peer.joined", data =>
            {
                var joined =
                    data.DeserializePayload<PeerJoinedPayload>();

                SendInitialValue(joined.peerId);
            });
        }
        else
        {
            Subscribe("sync", e =>
            {
                var data =
                    e.DeserializePayload<NetworkSynchronizerData>();

                if (!variableMap.TryGetValue(
                        data.id,
                        out var variable))
                {
                    Debug.LogWarning(
                        $"[NetworkSynchronizer] " +
                        $"Variable ID '{data.id}' not found.",
                        this
                    );

                    return;
                }

                try
                {
                    object value = data.GetValue();

                    variable.SyncValue(value);
                }
                catch (Exception ex)
                {
                    Debug.LogError(
                        $"[NetworkSynchronizer] " +
                        $"Failed syncing '{data.id}'.\n{ex}",
                        this
                    );
                }
            });
        }
    }


    private void Awake()
    {
        BuildVariableMap();
    }


    private void Update()
    {
        if (!((syncMode == SyncMode.OnlyHostSend&&IsHost) || syncMode == SyncMode.SendOnly))
            return;

        timer += Time.deltaTime;

        float networkUpdateInterval =
            1f / Mathf.Max(networkUpdateFps, 1);

        if (timer < networkUpdateInterval)
            return;

        // 프레임 드랍 시 누적 오차를 덜 만들기 위해
        // 0으로 만드는 대신 interval만큼 빼줌.
        timer -= networkUpdateInterval;

        NetworkUpdate();
    }


    private void BuildVariableMap()
    {
        variableMap.Clear();

        foreach (var variable in variables)
        {
            if (variable == null)
                continue;

            if (string.IsNullOrWhiteSpace(variable.id))
            {
                Debug.LogWarning(
                    "[NetworkSynchronizer] " +
                    "Variable has an empty ID.",
                    this
                );

                continue;
            }

            if (variableMap.ContainsKey(variable.id))
            {
                Debug.LogError(
                    $"[NetworkSynchronizer] " +
                    $"Duplicate Variable ID: '{variable.id}'",
                    this
                );

                continue;
            }

            variableMap.Add(variable.id, variable);
        }
    }


    private void NetworkUpdate()
    {
        for (int i = 0; i < variables.Count; i++)
        {
            var variable = variables[i];

            if (variable == null)
                continue;

            if (string.IsNullOrWhiteSpace(variable.id))
                continue;

            if (!variable.IsUpdated(
                    out var nextValue,
                    out _))
            {
                continue;
            }

            var data =
                NetworkSynchronizerData.From(
                    variable.id,
                    nextValue
                );

            SendEvent(
                "sync",
                EventAudience.Others,
                data
            );
        }
    }


    private void SendInitialValue(string peerId)
    {
        foreach (var variable in variables)
        {
            if (variable == null)
                continue;

            if (string.IsNullOrWhiteSpace(variable.id))
                continue;

            try
            {
                object value = variable.GetValue();

                var data =
                    NetworkSynchronizerData.From(
                        variable.id,
                        value
                    );

                SendEvent(
                    "sync",
                    peerId,
                    data
                );
            }
            catch (Exception ex)
            {
                Debug.LogError(
                    $"[NetworkSynchronizer] " +
                    $"Failed sending initial value " +
                    $"for '{variable.id}'.\n{ex}",
                    this
                );
            }
        }
    }


    // =========================================================
    // Network Data
    // =========================================================

    [Serializable]
    public struct NetworkSynchronizerData
    {
        // NetworkIdentity 내부에서 unique한 ID
        public string id;

        public string type;
        public string value;


        public object GetValue()
        {
            return type switch
            {
                "sbyte" =>
                    sbyte.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "byte" =>
                    byte.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "short" =>
                    short.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "ushort" =>
                    ushort.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "int" =>
                    int.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "uint" =>
                    uint.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "long" =>
                    long.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "ulong" =>
                    ulong.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "float" =>
                    float.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "double" =>
                    double.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "decimal" =>
                    decimal.Parse(
                        value,
                        CultureInfo.InvariantCulture
                    ),

                "bool" =>
                    bool.Parse(value),

                "char" =>
                    char.Parse(value),

                "string" =>
                    value,


                // =========================
                // Unity 타입
                // =========================

                "Vector2" =>
                    JsonUtility.FromJson<Vector2>(value),

                "Vector3" =>
                    JsonUtility.FromJson<Vector3>(value),

                "Vector4" =>
                    JsonUtility.FromJson<Vector4>(value),

                "Quaternion" =>
                    JsonUtility.FromJson<Quaternion>(value),


                _ => throw new InvalidOperationException(
                    $"Unsupported network type: '{type}'"
                )
            };
        }


        public static NetworkSynchronizerData From(
            string id,
            object value)
        {
            if (value == null)
            {
                throw new InvalidOperationException(
                    $"Cannot synchronize null value. ID: '{id}'"
                );
            }

            return new NetworkSynchronizerData
            {
                id = id,

                type = GetTypeName(value),

                value = SerializeValue(value)
            };
        }


        private static string GetTypeName(object value)
        {
            return value switch
            {
                sbyte => "sbyte",
                byte => "byte",
                short => "short",
                ushort => "ushort",

                int => "int",
                uint => "uint",

                long => "long",
                ulong => "ulong",

                float => "float",
                double => "double",
                decimal => "decimal",

                bool => "bool",
                char => "char",
                string => "string",

                Vector2 => "Vector2",
                Vector3 => "Vector3",
                Vector4 => "Vector4",

                Quaternion => "Quaternion",

                _ => throw new InvalidOperationException(
                    $"Unsupported type: {value.GetType()}"
                )
            };
        }


        private static string SerializeValue(object value)
        {
            return value switch
            {
                sbyte v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                byte v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                short v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                ushort v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                int v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                uint v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                long v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                ulong v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                float v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                double v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                decimal v =>
                    v.ToString(
                        CultureInfo.InvariantCulture
                    ),

                bool v =>
                    v.ToString(),

                char v =>
                    v.ToString(),

                string v =>
                    v,

                Vector2 v =>
                    JsonUtility.ToJson(v),

                Vector3 v =>
                    JsonUtility.ToJson(v),

                Vector4 v =>
                    JsonUtility.ToJson(v),

                Quaternion v =>
                    JsonUtility.ToJson(v),

                _ => throw new InvalidOperationException(
                    $"Unsupported type: {value.GetType()}"
                )
            };
        }
    }


    // =========================================================
    // Variable Reference
    // =========================================================

    [Serializable]
    public class VariableReference
    {
        [Tooltip(
            "같은 NetworkIdentity 내부에서 유일해야 하는 ID"
        )]
        [SerializeField]
        public string id;

        [SerializeField]
        public Component component;

        [Tooltip(
            "Field 또는 Property 이름. " +
            "예: armPitch, position, rotation, localPosition"
        )]
        [SerializeField]
        public string name;


        private FieldInfo fieldInfo;
        private PropertyInfo propertyInfo;

        private object prevValue;


        public bool IsUpdated(
            out object nextValue,
            out object previousValue)
        {
            previousValue = prevValue;

            nextValue = GetValue();

            if (AreEqual(prevValue, nextValue))
                return false;

            prevValue = nextValue;

            return true;
        }


        private void Resolve()
        {
            if (fieldInfo != null ||
                propertyInfo != null)
            {
                return;
            }

            if (component == null)
            {
                throw new InvalidOperationException(
                    $"Component is null. " +
                    $"Variable ID: '{id}'"
                );
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"Variable name is empty. " +
                    $"Variable ID: '{id}'"
                );
            }

            Type componentType =
                component.GetType();

            const BindingFlags flags =
                BindingFlags.Instance |
                BindingFlags.Public |
                BindingFlags.NonPublic;


            // =========================
            // 1. Field 검색
            // =========================

            fieldInfo =
                componentType.GetField(
                    name,
                    flags
                );

            if (fieldInfo != null)
                return;


            // =========================
            // 2. Property 검색
            // =========================

            propertyInfo =
                componentType.GetProperty(
                    name,
                    flags
                );

            if (propertyInfo != null)
                return;


            throw new InvalidOperationException(
                $"Field or Property '{name}' " +
                $"could not be found on " +
                $"'{componentType.Name}'. " +
                $"Variable ID: '{id}'"
            );
        }


        public object GetValue()
        {
            Resolve();

            if (fieldInfo != null)
            {
                return fieldInfo.GetValue(
                    component
                );
            }

            if (propertyInfo != null)
            {
                if (!propertyInfo.CanRead)
                {
                    throw new InvalidOperationException(
                        $"Property '{name}' is not readable."
                    );
                }

                return propertyInfo.GetValue(
                    component
                );
            }

            throw new InvalidOperationException(
                $"Variable '{id}' is unresolved."
            );
        }


        public void SyncValue(object value)
        {
            Resolve();

            if (fieldInfo != null)
            {
                fieldInfo.SetValue(
                    component,
                    value
                );

                // 자기 값도 갱신해서
                // Host 전환 같은 상황에서 불필요한 change 방지
                prevValue = value;

                return;
            }


            if (propertyInfo != null)
            {
                if (!propertyInfo.CanWrite)
                {
                    throw new InvalidOperationException(
                        $"Property '{name}' is read-only."
                    );
                }

                propertyInfo.SetValue(
                    component,
                    value
                );

                prevValue = value;

                return;
            }


            throw new InvalidOperationException(
                $"Variable '{id}' is unresolved."
            );
        }


        // =========================
        // 변화 감지
        // =========================

        private static bool AreEqual(
            object a,
            object b)
        {
            if (a == null && b == null)
                return true;

            if (a == null || b == null)
                return false;


            // Vector3는 아주 작은 변화 때문에
            // 매번 패킷 보내는 것을 어느 정도 방지
            if (a is Vector3 va &&
                b is Vector3 vb)
            {
                return
                    (va - vb).sqrMagnitude
                    < 0.000001f;
            }


            if (a is Vector2 v2a &&
                b is Vector2 v2b)
            {
                return
                    (v2a - v2b).sqrMagnitude
                    < 0.000001f;
            }


            if (a is Vector4 v4a &&
                b is Vector4 v4b)
            {
                return
                    (v4a - v4b).sqrMagnitude
                    < 0.000001f;
            }


            if (a is Quaternion qa &&
                b is Quaternion qb)
            {
                return
                    Quaternion.Angle(qa, qb)
                    < 0.01f;
            }


            if (a is float fa &&
                b is float fb)
            {
                return
                    Mathf.Abs(fa - fb)
                    < 0.0001f;
            }


            return Equals(a, b);
        }
    }
}