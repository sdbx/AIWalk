using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

public class Room1PuzzleController : MonoBehaviour
{
  [SerializeField]
  private TextMeshProUGUI monitorTextField;

  [SerializeField]
  private SimpleButton buttonPrefab;

  [SerializeField]
  private GameObject keyboardRoot;

  [SerializeField]
  private float buttonSpaceSize = 0.22f;

  [SerializeField]
  private Bulb ledPrefab;

  [SerializeField]
  private GameObject ledsRoot;
  [SerializeField]
  private float ledSpaceSize = 0.05f;

  [Header("Puzzle")]
  [SerializeField]
  private string monitorText = "hello\nworld";

  [SerializeField]
  private string answer = "test";

  void Awake()
  {
    SetupMonitor();
    CreateKeyboard();
    CreateLeds();
    BindKeyboardAndLeds();
  }

  void SetupMonitor()
  {
    monitorTextField.text = monitorText;
  }

  void CreateKeyboard()
  {
    var lines = monitorText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
    var height = lines.Length;
    var index = 0;

    for (var y = 0; y < height; y++)
    {
      var line = lines[y];
      var width = line.Length;
      for (var x = 0; x < width; x++)
      {
        var buttonIndex = index++;
        var button = Instantiate(buttonPrefab, keyboardRoot.transform.position + new Vector3((x - (width - 1) * 0.5f) * buttonSpaceSize, 0, -(y - (height - 1) * 0.5f) * buttonSpaceSize), Quaternion.identity, keyboardRoot.transform);

        var networkIdentity = button.AddComponent<NetworkIdentity>();
        networkIdentity.SetId($"puzzle-1-btn-{buttonIndex}");

        var interactable = button.GetComponent<Interactable>();
        interactable.networkIdentity = networkIdentity;

        button.OnPressed.AddListener(() => { ReleaseAllExceptMe(buttonIndex); });
      }
    }
  }

  void CreateLeds()
  {
    var count = answer.Length;

    for (var i = 0; i < count; i++)
    {
      var led = Instantiate(ledPrefab, ledsRoot.transform.position + new Vector3(i * ledSpaceSize, 0, 0), Quaternion.identity, ledsRoot.transform);

      var networkIdentity = led.AddComponent<NetworkIdentity>();
      networkIdentity.SetId($"puzzle-1-led-{i}");
    }
  }

  void BindKeyboardAndLeds()
  {
    var lines = monitorText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries).Select(line => line.ToCharArray()).ToArray();
    var height = lines.Length;

    var answerIndices = answer
      .Select((character, index) => (character, index))
      .GroupBy(x => x.character)
      .ToDictionary(
      g => g.Key,
      g => new Queue<int>(g.Select(x => x.index))
    );
    var index = 0;

    for (var y = 0; y < height; y++)
    {
      var line = lines[y];
      var width = line.Length;
      for (var x = 0; x < width; x++)
      {
        var character = line[x];
        var buttonIndex = index++;

        if (!answerIndices.TryGetValue(character, out var indices) || indices.Count == 0) continue;

        var ledIndex = indices.Dequeue();
        Debug.Log($"{buttonIndex} -> {ledIndex}");

        var button = keyboardRoot.transform.GetChild(buttonIndex).GetComponent<SimpleButton>();
        var led = ledsRoot.transform.GetChild(ledIndex).GetComponent<Bulb>();
        button.OnPressed.AddListener(led.TurnOn);
        button.OnReleased.AddListener(led.TurnOff);
      }
    }
  }

  void ReleaseAllExceptMe(int index)
  {
    for (int i = 0; i < keyboardRoot.transform.childCount; i++)
    {
      if (i == index) continue;
      var button = keyboardRoot.transform.GetChild(i).GetComponent<SimpleButton>();
      button.Release();
    }
  }
}
