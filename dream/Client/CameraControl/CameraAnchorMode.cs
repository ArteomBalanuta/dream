namespace Dream.Client.CameraControl;

// Follow: the view target is the player focus plus a pan offset, so it moves with the player.
// Free: the view target stays where it is in the world while the player walks (still leashed).
public enum CameraAnchorMode : byte
{
    Follow,
    Free
}
