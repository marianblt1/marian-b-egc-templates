using OpenTK.Mathematics;

namespace Egc;

/// <summary>
/// Poziția, rotația și scalarea unui obiect din scenă. Din ele se construiește matricea model,
/// care duce vârfurile obiectului din object space în world space (secțiunea 5).
/// </summary>
public class Transform2D
{
    public Vector2 Position = Vector2.Zero;   // translația, în unități de lume
    public float Rotation = 0.0f;             // unghiul, în GRADE, pozitiv = sens trigonometric
    public Vector2 Scale = Vector2.One;       // factorii de scalare pe x și pe y
    public float Depth = 0.0f;                // coordonata z; contează doar în perspectivă (secțiunea 7.2)

    // T1: ordinea este scalare -> rotatie -> translatie. Daca translatia ar fi aplicata inaintea
    // rotatiei, obiectul ar fi mutat intai in Position, iar rotatia (care se face fata de origine)
    // l-ar plimba pe un arc de cerc in jurul originii lumii, in loc sa se roteasca pe loc,
    // in jurul propriului centru; la fel, scalarea ar apropia/departa obiectul de origine.
    public Matrix4 ModelMatrix =>
        Matrix4.CreateScale(Scale.X, Scale.Y, 1.0f)
      * Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(Rotation))
      * Matrix4.CreateTranslation(Position.X, Position.Y, Depth);

    /// <summary>O copie independentă (folosită pentru readucerea scenei în starea inițială).</summary>
    public Transform2D Clone() => (Transform2D)MemberwiseClone();
}
