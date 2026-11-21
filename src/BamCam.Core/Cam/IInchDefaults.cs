namespace BamCam.Core.Cam;

/// <summary>
/// An operation only BamCam has, with starting values of its own for inch drawings (CamBam's operations take theirs from
/// the styles). Created in an inch drawing, it gets them; opened from a file that leaves out a default value, the inch
/// value is used for it too (issue 32).
/// </summary>
public interface IInchDefaults
{
    /// <summary>Sets the starting values for inch drawings.</summary>
    void UseInchDefaults();
}
