namespace Dream.Simulation.Persistence;

public interface IRubbleRepository
{
    IReadOnlyList<PersistedCubeFracturePiece> LoadCubeFracturePieces();

    void SaveCubeFracturePiece(PersistedCubeFracturePiece piece);

    void DeleteCubeFracturePieces(int sourceCubeId);
}
