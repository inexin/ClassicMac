namespace ClassicMac.Files;

/// <summary>
/// How far a long volume operation has come (First Aid, Defragment, Resize): step <paramref name="Step"/> of
/// <paramref name="Steps"/>, and what it is doing in words ("Checking catalog file.", "File 12 of 21").
/// </summary>
public readonly record struct VolumeProgress(int Step, int Steps, string Text);
