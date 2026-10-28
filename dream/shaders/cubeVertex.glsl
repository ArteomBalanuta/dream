#version 330 core

layout (location = 0) in vec3 aPos;
layout (location = 1) in vec3 aNormal;
layout (location = 3) in vec2 aTexCoord0;
layout (location = 4) in vec4 aJoints;
layout (location = 5) in vec4 aWeights;
layout (location = 6) in mat4 instanceModel;

uniform mat4 model;
// transpose(inverse(mat3(model))), computed on the CPU once per draw (NormalMatrix.FromModel) and
// uploaded with `model` by Shader.SetModelMatrix. Used by non-instanced draws only.
uniform mat3 normalMatrix;
uniform mat4 view;
uniform mat4 projection;
uniform bool useSkinning;
uniform bool useInstancing;
uniform mat4 jointMatrices[64];

out vec2 texCoord0;
out vec3 normal;
out vec3 fragPos;

int safeJointIndex(float rawIndex)
{
    return clamp(int(rawIndex + 0.5), 0, 63);
}

// Instanced draws carry their model matrix per instance, so there is no per-draw normal matrix.
// The cofactor matrix of mat3(m) is det * transpose(inverse(mat3(m))): three cross products give
// the normal direction for rotation and any (non-uniform) scale without inverting a matrix. The
// sign of det keeps mirrored transforms pointing the same way; the fragment shader normalizes.
mat3 cofactorNormalMatrix(mat4 m)
{
    vec3 c0 = m[0].xyz;
    vec3 c1 = m[1].xyz;
    vec3 c2 = m[2].xyz;
    mat3 cofactor = mat3(cross(c1, c2), cross(c2, c0), cross(c0, c1));
    return dot(c0, cofactor[0]) < 0.0 ? -cofactor : cofactor;
}

void main()
{
    vec4 localPosition = vec4(aPos, 1.0);
    vec3 localNormal = aNormal;

    if (useSkinning) {
        mat4 skinMatrix =
            (aWeights.x * jointMatrices[safeJointIndex(aJoints.x)]) +
            (aWeights.y * jointMatrices[safeJointIndex(aJoints.y)]) +
            (aWeights.z * jointMatrices[safeJointIndex(aJoints.z)]) +
            (aWeights.w * jointMatrices[safeJointIndex(aJoints.w)]);

        localPosition = skinMatrix * localPosition;
        localNormal = mat3(skinMatrix) * localNormal;
    }

    mat4 resolvedModel = useInstancing ? instanceModel : model;
    mat3 resolvedNormalMatrix = useInstancing ? cofactorNormalMatrix(instanceModel) : normalMatrix;

    fragPos = vec3(resolvedModel * localPosition);
    texCoord0 = aTexCoord0;
    normal = resolvedNormalMatrix * localNormal;
    
    gl_Position = projection * view * vec4(fragPos, 1.0);
}
