#version 330 core

layout (location = 0) in vec3 aPos;
layout (location = 1) in vec3 aNormal;
layout (location = 3) in vec2 aTexCoord0;
layout (location = 4) in vec4 aJoints;
layout (location = 5) in vec4 aWeights;
layout (location = 6) in mat4 instanceModel;

uniform mat4 model;
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

    fragPos = vec3(resolvedModel * localPosition);
    texCoord0 = aTexCoord0;
    normal = mat3(transpose(inverse(resolvedModel))) * localNormal;
    
    gl_Position = projection * view * vec4(fragPos, 1.0);
}
