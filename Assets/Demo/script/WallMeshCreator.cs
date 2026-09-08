using System.Collections.Generic;
using LibTessDotNet;
using System.Linq;

public static class WallMeshCreator
{
    public static (UnityEngine.Vector2[] vertices, int[] triangles) CreateWallMesh(List<UnityEngine.Vector2> outer, List<List<UnityEngine.Vector2>> inner)
    {
        if(outer == null || outer.Count < 3)
        {
            throw new System.ArgumentException("Outer contour must have at least 3 points.");
        }

        if(inner != null)
        {
            foreach(var hole in inner)
            {
                if(hole == null || hole.Count < 3)
                {
                    throw new System.ArgumentException("Each inner contour must have at least 3 points.");
                }
            }
        }
        else
        {
            inner = new List<List<UnityEngine.Vector2>>();
        }
        


        var tess = new Tess();

        tess.AddContour(ToContourVertices(outer), ContourOrientation.CounterClockwise);

        foreach (var hole in inner)
        {
            tess.AddContour(ToContourVertices(hole), ContourOrientation.Clockwise);
        }

        tess.Tessellate(WindingRule.EvenOdd, ElementType.Polygons, 3);

        return (tess.Vertices.Select(v => new UnityEngine.Vector2(v.Position.X, v.Position.Y)).ToArray(), tess.Elements);
    }

    private static ContourVertex[] ToContourVertices(IReadOnlyList<UnityEngine.Vector2> points)
    {
        ContourVertex[] result = new ContourVertex[points.Count];

        for (int i = 0; i < points.Count; i++)
        {
            UnityEngine.Vector2 p = points[i];

            result[i] = new ContourVertex
            {
                Position = new Vec3(p.x, p.y, 0)
            };
        }

        return result;
    }
}