using System.Collections.Generic;
using System.Linq;
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
    [SerializeField] private Vector3 size = Vector3.one;

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

        List<(Vector2, int[])>[] wallVertices = new List<(Vector2, int[])>[6];
        List<DoorData>[] doorData = new List<DoorData>[]{right, left, up, down, forward, back};

        List<Matrix4x4> doorOffsets = new List<Matrix4x4>();
        //right
        doorOffsets.Add(Matrix4x4.TRS(new Vector3(size.x/2, 0, 0), Quaternion.identity, Vector3.one));
        //left
        doorOffsets.Add(Matrix4x4.TRS(new Vector3(-size.x/2, 0, 0), Quaternion.identity, Vector3.one));
        //up
        doorOffsets.Add(Matrix4x4.TRS(new Vector3(0, size.y/2, 0), Quaternion.Euler(0, 0, 90), Vector3.one));
        //down
        doorOffsets.Add(Matrix4x4.TRS(new Vector3(0, -size.y/2, 0), Quaternion.Euler(0, 0, 90), Vector3.one));
        //forward
        doorOffsets.Add(Matrix4x4.TRS(new Vector3(0, 0, size.z/2), Quaternion.Euler(0, 90, 0), Vector3.one));
        //back
        doorOffsets.Add(Matrix4x4.TRS(new Vector3(0, 0, -size.z/2), Quaternion.Euler(0, 90, 0), Vector3.one));


        for (int i = 0; i < wallVertices.Length; i++)
        {
            wallVertices[i] = new List<(Vector2, int[])>();
            var outer = new List<Vector2>(){new Vector2(-size.x/2, -size.y/2), new Vector2(size.x/2, -size.y/2), new Vector2(size.x/2, size.y/2), new Vector2(-size.x/2, size.y/2)};
            var (vertices, triangles) = WallMeshCreator.CreateWallMesh(outer, doorData[i].Select(d => d.GetDoorVertices()).ToList());

            for (int j = 0; j < vertices.Length; j++)
            {
                Vector3 vertex = doorOffsets[i].MultiplyPoint3x4(new Vector3(vertices[j].x, vertices[j].y, 0));
                wallVertices[i].Add((new Vector2(vertex.x, vertex.y), triangles));
            }
            //add triangles to the mesh with offset indices
            int vertexOffset = mesh.vertexCount;
            foreach (var (vertex, tris) in wallVertices[i])
            {
                mesh.vertices = mesh.vertices.Concat(new Vector3[]{new Vector3(vertex.x, vertex.y, doorOffsets[i].GetColumn(3).z)}).ToArray();
                mesh.triangles = mesh.triangles.Concat(tris.Select(t => t + vertexOffset)).ToArray();
            }

        }

        
        GetComponent<MeshFilter>().sharedMesh = mesh;
    }
}