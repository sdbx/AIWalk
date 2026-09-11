using System;
using UnityEngine;

public class Bulb : MonoBehaviour
{
    [SerializeField]
    private Renderer renderer;

    [SerializeField]
    private float speed = 5f;
    [SerializeField]
    private float intensity = 5f;
    [SerializeField]
    private Color emissionColor;

    [SerializeField]
    bool on = false;
    // Start is called once before the first execution of Update after the MonoBehaviour is created

    private static readonly int EmissionColorId = Shader.PropertyToID("_EmissionColor");

    float currentIntensity = 0f;

    MaterialPropertyBlock propertyBlock;

    void Start()
    {
        propertyBlock = new MaterialPropertyBlock();
        var targetMaterial = renderer.materials[0];
        targetMaterial.EnableKeyword("_EMISSION");
        targetMaterial.SetColor("_BaseColor", emissionColor);
    }

    // Update is called once per frame
    void Update()
    {
        currentIntensity = Mathf.Lerp(currentIntensity, on ? intensity : 0f, Time.deltaTime * speed);

        renderer.GetPropertyBlock(propertyBlock, 0);

        propertyBlock.SetColor(EmissionColorId, emissionColor * Mathf.LinearToGammaSpace(currentIntensity));
        
        renderer.SetPropertyBlock(propertyBlock, 0);
    }

    public void TurnOn()
    {
        on = true;
    }

    public void TurnOff()
    {
        on = false;
    }

    public void Toggle()
    {
        on = !on;
    }
}
