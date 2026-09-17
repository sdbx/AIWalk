using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using AIWalk.Networking;
using UnityEngine;

[RequireComponent(typeof(NetworkIdentity))]
public class NetworkSynchronizer : MonoBehaviour
{
  [SerializeField]
  private List<VariableReference> variables = new();

  NetworkClient networkClient;
  NetworkIdentity networkIdentity;
  bool host;

  void Start()
  {
    NetworkClient.GetInstance(ref networkClient);
    networkClient.onServerConnected.AddListener(OnNetworkReady);

    networkIdentity = GetComponent<NetworkIdentity>();
  }

  void OnNetworkReady()
  {
    host = networkClient.IsHost;

    if (host)
    {
      networkClient.Subscribe("peer.joined", data =>
      {
        var joined = data.DeserializePayload<PeerJoinedPayload>();
        SendInitialValue(joined.peerId);
      });
    }

    if (!host)
    {
      networkClient.Subscribe($"sync/{networkIdentity.Id}", e =>
      {
        var data = e.DeserializePayload<NetworkSynchronizerData>();
        var variable = variables[data.index];
        object value = data.GetValue();
        variable.SyncValue(value);
      });
    }
  }

  [SerializeField]
  [Range(1, 60)]
  private int networkUpdateFps = 20;
  float timer = 0f;
  void Update()
  {
    if (!host) return;

    timer += Time.deltaTime;

    var networkUpdateInterval = 1f / networkUpdateFps;

    if (timer >= networkUpdateInterval)
    {
      timer -= networkUpdateInterval;
      NetworkUpdate();
    }
  }

  void NetworkUpdate()
  {
    for (var i = 0; i < variables.Count; i++)
    {
      var variable = variables[i];
      if (!variable.IsUpdated(out var nextValue, out var prevValue)) continue;
      var data = NetworkSynchronizerData.From(i, variable.name, nextValue);
      networkClient.SendEvent($"sync/{networkIdentity.Id}", EventAudience.Others, data);
    }
  }

  void SendInitialValue(string peerId)
  {
    for (var i = 0; i < variables.Count; i++)
    {
      var variable = variables[i];
      var nextValue = variable.GetValue();
      var data = NetworkSynchronizerData.From(i, variable.name, nextValue);
      networkClient.SendEventTo($"sync/{networkIdentity.Id}", peerId, data);
    }
  }

  [Serializable]
  public struct NetworkSynchronizerData
  {
    public int index;
    public string name;

    public string type;

    public string value;

    public object GetValue()
    {
      return type switch
      {
        "sbyte" => sbyte.Parse(value, CultureInfo.InvariantCulture),
        "byte" => byte.Parse(value, CultureInfo.InvariantCulture),
        "short" => short.Parse(value, CultureInfo.InvariantCulture),
        "ushort" => ushort.Parse(value, CultureInfo.InvariantCulture),
        "int" => int.Parse(value, CultureInfo.InvariantCulture),
        "uint" => uint.Parse(value, CultureInfo.InvariantCulture),
        "long" => long.Parse(value, CultureInfo.InvariantCulture),
        "ulong" => ulong.Parse(value, CultureInfo.InvariantCulture),

        "float" => float.Parse(value, CultureInfo.InvariantCulture),
        "double" => double.Parse(value, CultureInfo.InvariantCulture),
        "decimal" => decimal.Parse(value, CultureInfo.InvariantCulture),

        "bool" => bool.Parse(value),
        "char" => char.Parse(value),
        "string" => value,

        _ => throw new InvalidOperationException($"Unsupported type: {type}")
      };
    }

    public static NetworkSynchronizerData From(int index, string name, object value)
    {
      return new()
      {
        index = index,
        name = name,
        type = value switch
        {
          sbyte _ => "sbyte",
          byte _ => "byte",
          short _ => "short",
          ushort _ => "ushort",
          int _ => "int",
          uint _ => "uint",
          long _ => "long",
          ulong _ => "ulong",

          float _ => "float",
          double _ => "double",
          decimal _ => "decimal",

          bool _ => "bool",
          char _ => "char",
          string _ => "string",

          _ => throw new InvalidOperationException($"Unsupported type: {value.GetType()}")
        },
        value = value switch
        {
          sbyte v => v.ToString(CultureInfo.InvariantCulture),
          byte v => v.ToString(CultureInfo.InvariantCulture),
          short v => v.ToString(CultureInfo.InvariantCulture),
          ushort v => v.ToString(CultureInfo.InvariantCulture),
          int v => v.ToString(CultureInfo.InvariantCulture),
          uint v => v.ToString(CultureInfo.InvariantCulture),
          long v => v.ToString(CultureInfo.InvariantCulture),
          ulong v => v.ToString(CultureInfo.InvariantCulture),

          float v => v.ToString(CultureInfo.InvariantCulture),
          double v => v.ToString(CultureInfo.InvariantCulture),
          decimal v => v.ToString(CultureInfo.InvariantCulture),

          bool v => v.ToString(),
          char v => v.ToString(),
          string v => v,

          _ => throw new InvalidOperationException($"Unsupported type: {value.GetType()}")
        },
      };
    }
  }

  [Serializable]
  public class VariableReference
  {
    [SerializeField]
    public Component component;
    [field: SerializeField]
    public string name;

    FieldInfo fieldInfo;
    object prevValue;

    public bool IsUpdated(out object nextValue, out object prevValue)
    {
      prevValue = this.prevValue;
      nextValue = GetValue();

      if (Equals(prevValue, nextValue))
        return false;
      this.prevValue = nextValue;

      return true;
    }

    FieldInfo Resolve()
    {
      if (fieldInfo == null)
        fieldInfo = component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
      return fieldInfo;
    }

    public object GetValue()
    {
      return Resolve().GetValue(component);
    }

    public void SyncValue(object value)
    {
      Resolve().SetValue(component, value);
    }
  }
}