# Quick Start Guide

## Installation

### 1. Install Package

PerSpec is published on [OpenUPM](https://openupm.com/packages/com.digitraver.perspec/). The CLI
is the easiest route, because it adds the scoped registry and every required scope for you:

```bash
npm install -g openupm-cli
openupm add com.digitraver.perspec
```

<details>
<summary><b>Manual installation (alternative)</b></summary>

Add the following to `Packages/manifest.json`. **All three scopes are required.** PerSpec depends
on `com.cysharp.unitask` and `com.gilzoide.sqlite-net`, which also come from OpenUPM. Leave either
scope out and Unity asks the default Unity registry instead, which does not host them, and you get
`Package [com.gilzoide.sqlite-net@1.3.1] cannot be found`.

```json
"scopedRegistries": [
  {
    "name": "package.openupm.com",
    "url": "https://package.openupm.com",
    "scopes": [
      "com.digitraver.perspec",
      "com.cysharp.unitask",
      "com.gilzoide.sqlite-net"
    ]
  }
],
"dependencies": {
  "com.digitraver.perspec": "1.8.1"
}
```

> **Note:** Use an exact version. Unity's project manifest does not support npm-style version
> ranges such as `^1.8.0`. To upgrade, change the number and let Unity re-resolve.

</details>

If a dependency ever fails to resolve, run `Tools > PerSpec > Repair Package Dependencies`. It
reports exactly which package is missing and why, and stays available even when the rest of the
PerSpec menu does not.

### 2. Initialize PerSpec

```bash
# Open Unity and run:
Tools > PerSpec > Initialize PerSpec

# Or use the automatic prompt that appears on first launch
```

This will:
- Create `PerSpec/` directory in your project root
- Initialize the SQLite database at `PerSpec/test_coordination.db`

## Writing Your First Test

### Simple Unity Test

```csharp
using NUnit.Framework;
using UnityEngine.TestTools;
using PerSpec.Runtime.Unity;
using Cysharp.Threading.Tasks;
using System.Collections;

public class SimpleTests : UniTaskTestBase
{
    [UnityTest]
    public IEnumerator TestPlayerMovement() => UniTask.ToCoroutine(async () =>
    {
        // Arrange
        var player = new GameObject("Player");
        var controller = player.AddComponent<PlayerController>();
        
        // Act
        await controller.MoveAsync(Vector3.forward);
        await UniTask.Delay(100);
        
        // Assert
        Assert.AreEqual(Vector3.forward, player.transform.position);
        
        // Cleanup
        Object.DestroyImmediate(player);
    });
}
```

## Running Tests

### From Unity Editor

1. Open `Tools > PerSpec > Test Coordinator`
2. Tests will run automatically when triggered

### From Command Line

```bash
# From project root:
# Run all tests
python PerSpec/Coordination/Scripts/quick_test.py all -p edit --wait

# Run specific test class
python PerSpec/Coordination/Scripts/quick_test.py class MyTests -p edit --wait

# Check for errors
python PerSpec/Coordination/Scripts/quick_logs.py errors
```

## The 4-Step Workflow

Always follow this pattern:

```bash
# 1. Write your code/tests
# 2. Refresh Unity
python PerSpec/Coordination/Scripts/quick_refresh.py full --wait

# 3. Check for compilation errors
python PerSpec/Coordination/Scripts/quick_logs.py errors

# 4. Run tests
python PerSpec/Coordination/Scripts/quick_test.py all -p edit --wait
```

## Common Patterns

### Async Operations

```csharp
[UnityTest]
public IEnumerator TestAsyncOperation() => UniTask.ToCoroutine(async () =>
{
    await UniTask.Delay(1000);
    await UniTask.SwitchToMainThread();
    // Unity API calls here
});
```

### DOTS Testing

```csharp
public class DOTSTests : DOTSTestBase
{
    [Test]
    public void TestEntityCreation()
    {
        var entity = EntityManager.CreateEntity();
        EntityManager.AddComponentData(entity, new Translation());
        Assert.IsTrue(EntityManager.HasComponent<Translation>(entity));
    }
}
```

## Troubleshooting

### Tests Not Running

1. Check database connection: `Tools > PerSpec > Debug > Test Connection`
2. Verify polling: `Tools > PerSpec > Debug > Polling Status`
3. Clear pending: `Tools > PerSpec > Debug > Clear Pending Requests`

### Compilation Errors

Always check errors before running tests:
```bash
python PerSpec/Coordination/Scripts/quick_logs.py errors
```

### Console Logs

View all Unity logs:
```bash
python PerSpec/Coordination/Scripts/quick_logs.py latest -n 50
```

## Next Steps

- Read the [Unity Testing Guide](unity-test-guide.md)
- Learn about [DOTS Testing](dots-test-guide.md)
- Explore [Coordination System](coordination-guide.md)