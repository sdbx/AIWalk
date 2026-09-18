using System;
using UnityEngine;

[Serializable]
public class InterfaceBehavior<T> where T : class{
    [SerializeField]
    private MonoBehaviour value;

    public T Value=>value as T;
}