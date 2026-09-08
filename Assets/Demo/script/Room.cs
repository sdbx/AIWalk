using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;

[System.Serializable]
public class DoorData
{
    [SerializeField] private Vector2 position;
    [SerializeField] private Vector2 size;

    public List<Vector2> GetDoorVertices()
    {
        return new List<Vector2>()
        {
            new Vector2(position.x - size.x/2, position.y - size.y/2),
            new Vector2(position.x + size.x/2, position.y - size.y/2),
            new Vector2(position.x + size.x/2, position.y + size.y/2),
            new Vector2(position.x - size.x/2, position.y + size.y/2)
        };
    }
}




[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class Room : MonoBehaviour
{
    [SerializeField] private float size = 1f;

    [SerializeField] private List<DoorData> right = new();
    [SerializeField] private List<DoorData> left = new();
    [SerializeField] private List<DoorData> up = new();
    [SerializeField] private List<DoorData> down = new();
    [SerializeField] private List<DoorData> forward = new();
    [SerializeField] private List<DoorData> back = new();


    public void Generate()
    {
        Mesh mesh = new Mesh();
        mesh.name = "TestRoom";

        (List<Vector2>, List<int>)[] wallVertices = new (List<Vector2>, List<int>)[6];
        List<DoorData>[] doorData = new List<DoorData>[]{right, left, up, down, forward, back};


        List<Matrix4x4> doorOffsets = new List<Matrix4x4>();
        // right
        doorOffsets.Add(Matrix4x4.Rotate(Quaternion.Euler(0, 90, 0)));
        // left
        doorOffsets.Add(Matrix4x4.Rotate(Quaternion.Euler(0, -90, 0)));
        // up
        doorOffsets.Add(Matrix4x4.Rotate(Quaternion.Euler(-90, 0, 0)));
        // down
        doorOffsets.Add(Matrix4x4.Rotate(Quaternion.Euler(90, 0, 0)));
        // front
        doorOffsets.Add(Matrix4x4.Rotate(Quaternion.identity));
        // back
        doorOffsets.Add(Matrix4x4.Rotate(Quaternion.Euler(0, 180, 0)));



        
        for (int i = 0; i < wallVertices.Length; i++)
        {
            wallVertices[i] = (new List<Vector2>(), new List<int>());
            var outer = new List<Vector2>(){new Vector2(-size/2, -size/2), new Vector2(size/2, -size/2), new Vector2(size/2, size/2), new Vector2(-size/2, size/2)};
            var (vertices, triangles) = WallMeshCreator.CreateWallMesh(outer, doorData[i].Select(d => d.GetDoorVertices()).ToList());


            List<Vector3> newVertices = new List<Vector3>();

            for (int j = 0; j < vertices.Length; j++)
            {
                var vertex = vertices[j];
                Vector3 newVertex = doorOffsets[i].MultiplyPoint3x4(new Vector3(vertex.x, vertex.y, size/2));
                newVertices.Add(newVertex);
            }

            int vertexOffset = mesh.vertexCount;
            mesh.vertices = mesh.vertices.Concat(newVertices).ToArray();
            mesh.triangles = mesh.triangles.Concat(triangles.Select(t => t + vertexOffset)).ToArray();
        }

        GetComponent<MeshFilter>().sharedMesh = mesh;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        
    }
}