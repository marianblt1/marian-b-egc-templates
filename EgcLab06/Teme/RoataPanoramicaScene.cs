// Varianta C: roata panoramică
using OpenTK.Mathematics;
using OpenTK.Windowing.GraphicsLibraryFramework;

namespace Egc;

/// <summary>
/// Tema (varianta C): o roată panoramică cu 8 cabine, construită ierarhic.
///   butuc (pivot, se rotește) -> spițe, jantă, pivoți de cabină -> agățătoare + cabină.
/// Fiecare pivot de cabină se rotește invers față de roată, deci cabina rămâne verticală.
/// Toate mișcările folosesc doar timpul de joc (dt), deci pauza, scara timpului și blocarea (B)
/// funcționează. Tastele se citesc doar prin acțiuni cu nume.
/// </summary>
public sealed class RoataPanoramicaScene : Scene
{
    // --- Acțiunile scenei --------------------------------------------------------------------
    private const string SpeedUp = "mărește viteza țintă";
    private const string SpeedDown = "micșorează viteza țintă";
    private const string StopWheel = "oprește roata (lin)";
    private static readonly string[] DockActions =
        Enumerable.Range(1, CabinCount).Select(i => $"cabina {i} la peron").ToArray();

    // --- Geometria roții ---------------------------------------------------------------------
    private const int CabinCount = 8;
    private const int RimSegments = 24;
    private const float Radius = 4.0f;              // raza roții (butuc -> punct de prindere)
    private const float HubHeight = 5.0f;           // înălțimea butucului față de podea
    private const float CabinStep = 360.0f / CabinCount;
    private const float PlatformAngle = -90.0f;     // la ce unghi (în lume) este peronul: jos

    // --- Mișcarea roții ----------------------------------------------------------------------
    private const float MaxSpeed = 60.0f;           // grade / s (în valoare absolută)
    private const float TargetRate = 20.0f;         // grade / s², cât timp o săgeată este ținută
    private const float Smoothing = 2.0f;           // k din v ← v + (v_țintă − v)·(1 − e^(−k·dt))

    // --- Bonus: cabine care se leagănă (pendul amortizat) ----------------------------------------
    private const float SwingLength = 0.7f;         // lungimea pendulului
    private const float SwingGravity = 9.81f;
    private const float SwingDamping = 1.2f;        // amortizarea
    private const float SwingGain = 0.3f;           // cât de puternic "simte" cabina accelerația roții
    private const float SwingLimit = 0.7f;          // radiani (≈ 40°)
    private const float SwingStep = 0.01f;          // pasul maxim de integrare, în secunde

    private static readonly Color4[] CabinColors =
    {
        new(0.95f, 0.30f, 0.30f, 1.0f), new(0.95f, 0.60f, 0.20f, 1.0f),
        new(0.95f, 0.85f, 0.25f, 1.0f), new(0.45f, 0.85f, 0.35f, 1.0f),
        new(0.25f, 0.80f, 0.80f, 1.0f), new(0.35f, 0.55f, 1.00f, 1.0f),
        new(0.65f, 0.45f, 0.95f, 1.0f), new(0.95f, 0.45f, 0.75f, 1.0f),
    };

    private SceneObject _hub = null!;
    private readonly SceneObject[] _pivots = new SceneObject[CabinCount];
    private readonly float[] _swing = new float[CabinCount];       // unghiul de leagăn, în radiani
    private readonly float[] _swingSpeed = new float[CabinCount];  // viteza de leagăn, rad/s

    private float _angle = PlatformAngle;   // unghiul roții, în grade; la început cabina 1 este la peron
    private float _speed;                   // viteza unghiulară reală, grade / s
    private float _targetSpeed;             // viteza țintă, grade / s
    private float _previousSpeed;           // viteza din cadrul anterior (pentru accelerație)
    private Tween? _dock;                   // animația "adu cabina la peron" (pe X: unghiul roții)
    private int _dockCabin;

    public override string Name => "roata panoramică";

    public override string Status =>
        $"viteză {_speed:F0}°/s (țintă {_targetSpeed:F0}) | peron: cabina {CabinAtPlatform() + 1}";

    public override IEnumerable<string> ActionNames =>
        new[] { SpeedUp, SpeedDown, StopWheel }.Concat(DockActions);

    public override void Load(Input input)
    {
        CameraPosition = new Vector3(0.0f, 5.5f, 18.0f);
        CameraTarget = new Vector3(0.0f, 4.5f, 0.0f);

        input.Bind(SpeedUp, Keys.Up);
        input.Bind(SpeedDown, Keys.Down);
        input.Bind(StopWheel, Keys.Space);
        for (int i = 0; i < CabinCount; i++)
        {
            input.Bind(DockActions[i], Keys.D1 + i, Keys.KeyPad1 + i);
        }

        Mesh grid = AddMesh(Shapes.Grid(10, 1.0f, new Color4(0.30f, 0.33f, 0.38f, 1.0f)));
        Mesh cube = AddMesh(Shapes.ShadedCube());
        Mesh sphere = AddMesh(Shapes.ShadedSphere(1.0f, 32, 16));

        AddObject("podea", grid);
        BuildSupport(cube);
        BuildWheel(cube, sphere);
        ApplyPose();
    }

    // --- Construcția scenei --------------------------------------------------------------------

    /// <summary>Peronul și cele două picioare ale suportului (nu se rotesc).</summary>
    private void BuildSupport(Mesh cube)
    {
        SceneObject platform = AddObject("peron", cube);
        platform.Transform.Position = new Vector3(0.0f, 0.02f, 0.0f);
        platform.Transform.Scale = new Vector3(1.8f, 0.04f, 1.8f);
        platform.Tint = new Color4(0.55f, 0.55f, 0.6f, 1.0f);

        foreach (float side in new[] { -1.0f, 1.0f })
        {
            var foot = new Vector2(side * 2.6f, 0.0f);
            var top = new Vector2(0.0f, HubHeight);
            Vector2 d = top - foot;

            SceneObject leg = AddObject("picior", cube);
            leg.Transform.Position = new Vector3((foot.X + top.X) / 2.0f, (foot.Y + top.Y) / 2.0f, -0.7f);
            // axa Y a cubului trebuie să fie orientată pe direcția piciorului
            leg.Transform.Rotation = new Vector3(0.0f, 0.0f, MathHelper.RadiansToDegrees(MathF.Atan2(-d.X, d.Y)));
            leg.Transform.Scale = new Vector3(0.18f, d.Length, 0.18f);
            leg.Tint = new Color4(0.6f, 0.62f, 0.7f, 1.0f);
        }
    }

    /// <summary>Butucul și tot ce se rotește odată cu el: spițe, jantă, cabine.</summary>
    private void BuildWheel(Mesh cube, Mesh sphere)
    {
        _hub = AddObject("butuc", null);   // pivot: nu are formă, doar poartă rotația roții
        _hub.Transform.Position = new Vector3(0.0f, HubHeight, 0.0f);

        SceneObject hubBall = AddObject("capac butuc", sphere, _hub);
        hubBall.Transform.Scale = new Vector3(0.4f);
        hubBall.Tint = new Color4(0.9f, 0.9f, 0.95f, 1.0f);

        SceneObject axle = AddObject("axă", cube, _hub);
        axle.Transform.Position = new Vector3(0.0f, 0.0f, -0.35f);
        axle.Transform.Scale = new Vector3(0.14f, 0.14f, 0.9f);
        axle.Tint = new Color4(0.6f, 0.62f, 0.7f, 1.0f);

        // Jantă: poligon cu RimSegments laturi. Axa Y a cubului se aliniază cu tangenta cercului.
        float step = 360.0f / RimSegments;
        float halfStep = MathHelper.DegreesToRadians(step / 2.0f);
        for (int k = 0; k < RimSegments; k++)
        {
            float mid = (k + 0.5f) * step;                       // grade
            float midRad = MathHelper.DegreesToRadians(mid);
            float distance = Radius * MathF.Cos(halfStep);       // distanța de la centru la mijlocul laturii
            float length = 2.0f * Radius * MathF.Sin(halfStep);

            SceneObject piece = AddObject($"jantă {k}", cube, _hub);
            piece.Transform.Position = new Vector3(distance * MathF.Cos(midRad), distance * MathF.Sin(midRad), 0.0f);
            piece.Transform.Rotation = new Vector3(0.0f, 0.0f, mid);
            piece.Transform.Scale = new Vector3(0.1f, length * 1.05f, 0.1f);
            piece.Tint = new Color4(0.85f, 0.85f, 0.9f, 1.0f);
        }

        for (int i = 0; i < CabinCount; i++)
        {
            float phi = i * CabinStep;
            float rad = MathHelper.DegreesToRadians(phi);
            var rim = new Vector3(Radius * MathF.Cos(rad), Radius * MathF.Sin(rad), 0.0f);

            // Spița: de la centru la punctul de prindere.
            SceneObject spoke = AddObject($"spiță {i + 1}", cube, _hub);
            spoke.Transform.Position = rim * 0.5f;
            spoke.Transform.Rotation = new Vector3(0.0f, 0.0f, phi - 90.0f);
            spoke.Transform.Scale = new Vector3(0.08f, Radius, 0.08f);
            spoke.Tint = new Color4(0.7f, 0.72f, 0.8f, 1.0f);

            // Pivotul cabinei: la punctul de prindere, copil al butucului. Rotația lui (setată în
            // ApplyPose) o anulează pe cea a roții, deci cabina rămâne verticală.
            SceneObject pivot = AddObject($"pivot cabina {i + 1}", null, _hub);
            pivot.Transform.Position = rim;
            _pivots[i] = pivot;

            SceneObject hanger = AddObject($"agățătoare {i + 1}", cube, pivot);
            hanger.Transform.Position = new Vector3(0.0f, -0.2f, 0.0f);
            hanger.Transform.Scale = new Vector3(0.06f, 0.4f, 0.06f);
            hanger.Tint = new Color4(0.7f, 0.72f, 0.8f, 1.0f);

            // Cabina atârnă sub punctul de prindere: jos, la peron, atinge podeaua.
            SceneObject cabin = AddObject($"cabina {i + 1}", cube, pivot);
            cabin.Transform.Position = new Vector3(0.0f, -0.7f, 0.0f);
            cabin.Transform.Scale = new Vector3(0.9f, 0.6f, 0.9f);
            cabin.Tint = CabinColors[i];
        }
    }

    // --- Actualizarea, la fiecare cadru ------------------------------------------------------------

    public override void Update(float dt, Input input)
    {
        base.Update(dt, input);

        // 1. Tastele 1-8: aduc cabina aleasă la peron (Tween pe unghiul roții).
        for (int i = 0; i < CabinCount; i++)
        {
            if (input.WasPressed(DockActions[i]))
            {
                StartDock(i);
            }
        }

        // 2. Săgețile (ținute) schimbă viteza țintă; Space o aduce la zero. Preiau controlul de la Tween.
        float adjust = input.Axis(SpeedDown, SpeedUp);
        if (adjust != 0.0f && dt > 0.0f)
        {
            _dock = null;
            _targetSpeed = Math.Clamp(_targetSpeed + adjust * TargetRate * dt, -MaxSpeed, MaxSpeed);
        }
        if (input.WasPressed(StopWheel))
        {
            _dock = null;
            _targetSpeed = 0.0f;
        }

        // 3. Mișcarea roții.
        float oldAngle = _angle;
        if (_dock != null)
        {
            _dock.Update(dt);
            _angle = _dock.Value.X;
            if (dt > 0.0f)
            {
                _speed = (_angle - oldAngle) / dt;   // viteza rezultată din Tween (pentru leagăn și pentru Space)
            }
            if (_dock.IsFinished)
            {
                _dock = null;
                _speed = 0.0f;
                _targetSpeed = 0.0f;
                ConsolePanel.PrintMessage($"cabina {_dockCabin + 1} este la peron");
            }
        }
        else
        {
            // Viteza reală se apropie lin de cea țintă; formula dă același rezultat la orice FPS.
            _speed += (_targetSpeed - _speed) * (1.0f - MathF.Exp(-Smoothing * dt));
            _angle += _speed * dt;
            _angle = WrapDegrees(_angle);
        }

        // 4. Bonus: leagănul cabinelor, condus de accelerația roții.
        if (dt > 0.0f)
        {
            float acceleration = (_speed - _previousSpeed) / dt;   // grade / s²
            UpdateSwing(dt, acceleration);
        }
        _previousSpeed = _speed;

        ApplyPose();
    }

    /// <summary>Pornește animația care aduce cabina <paramref name="index"/> la peron, pe drumul cel mai scurt.</summary>
    private void StartDock(int index)
    {
        float wanted = PlatformAngle - index * CabinStep;           // unghiul roții cu cabina la peron
        float delta = WrapDegrees(wanted - _angle);                 // în [-180, 180): cel mult o jumătate de tur
        float duration = 0.6f + MathF.Abs(delta) / 120.0f;

        _dockCabin = index;
        _targetSpeed = 0.0f;
        _dock = new Tween(new Vector3(_angle, 0.0f, 0.0f), new Vector3(_angle + delta, 0.0f, 0.0f),
                          duration, Easing.EaseInOut);
        ConsolePanel.PrintMessage($"cabina {index + 1} coboară la peron ({delta:F0}°)");
    }

    /// <summary>
    /// Fiecare cabină este un pendul atârnat de punctul ei de prindere. Când roata accelerează, punctul de
    /// prindere accelerează pe orizontală cu A_x = −R·θ'' · sin(α), iar pendulul rămâne în urmă:
    /// s'' = −(g/L)·sin s − c·s' − (A_x/L)·cos s. Integrare Euler semi-implicită, în pași mici.
    /// </summary>
    private void UpdateSwing(float dt, float accelerationDegrees)
    {
        float accel = MathHelper.DegreesToRadians(Math.Clamp(accelerationDegrees, -300.0f, 300.0f));
        int steps = Math.Max(1, (int)MathF.Ceiling(dt / SwingStep));
        float h = dt / steps;

        for (int i = 0; i < CabinCount; i++)
        {
            float alpha = MathHelper.DegreesToRadians(_angle + i * CabinStep);   // unghiul cabinei în lume
            float ax = -Radius * accel * MathF.Sin(alpha) * SwingGain;           // accelerația orizontală

            for (int s = 0; s < steps; s++)
            {
                float a = -(SwingGravity / SwingLength) * MathF.Sin(_swing[i])
                          - SwingDamping * _swingSpeed[i]
                          - (ax / SwingLength) * MathF.Cos(_swing[i]);
                _swingSpeed[i] += a * h;
                _swing[i] += _swingSpeed[i] * h;
            }
            _swing[i] = Math.Clamp(_swing[i], -SwingLimit, SwingLimit);
        }
    }

    /// <summary>Copiază starea (unghiul roții, leagănul) în transformările obiectelor.</summary>
    private void ApplyPose()
    {
        _hub.Transform.Rotation = new Vector3(0.0f, 0.0f, _angle);
        for (int i = 0; i < CabinCount; i++)
        {
            // -_angle anulează rotația moștenită de la butuc; leagănul se adaugă peste.
            float swingDegrees = MathHelper.RadiansToDegrees(_swing[i]);
            _pivots[i].Transform.Rotation = new Vector3(0.0f, 0.0f, -_angle + swingDegrees);
        }
    }

    // --- Funcții ajutătoare -----------------------------------------------------------------------

    /// <summary>Aduce un unghi în [-180, 180).</summary>
    private static float WrapDegrees(float degrees)
    {
        float r = (degrees + 180.0f) % 360.0f;
        if (r < 0.0f)
        {
            r += 360.0f;
        }
        return r - 180.0f;
    }

    /// <summary>Indicele cabinei cea mai apropiată de peron.</summary>
    private int CabinAtPlatform()
    {
        int best = 0;
        float bestDifference = float.MaxValue;
        for (int i = 0; i < CabinCount; i++)
        {
            float difference = MathF.Abs(WrapDegrees(_angle + i * CabinStep - PlatformAngle));
            if (difference < bestDifference)
            {
                bestDifference = difference;
                best = i;
            }
        }
        return best;
    }

    public override IEnumerable<string> Describe()
    {
        foreach (string line in base.Describe()) yield return line;

        string mode = _dock != null ? $"aduce cabina {_dockCabin + 1} la peron ({_dock.Progress:P0})" : "liber";
        yield return $"Roata          : unghi {WrapDegrees(_angle):F1}°   viteză {_speed:F1}°/s   țintă {_targetSpeed:F1}°/s";
        yield return $"Peron          : cabina {CabinAtPlatform() + 1}   mod: {mode}";
        yield return "Leagăn cabine : " + string.Join("  ", _swing.Select(s => $"{MathHelper.RadiansToDegrees(s):F1}°"));
    }
}
